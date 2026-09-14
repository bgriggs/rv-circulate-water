using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CirculateWater;

/// <summary>
/// Outcome of an edit. <see cref="Config"/> is the settings in effect after a successful edit, or unchanged when it fails.
/// </summary>
internal sealed record ConfigEditResult(bool Success, IReadOnlyList<string> Errors, JsonObject Config);

/// <summary>
/// Reads and edits the CirculateWater settings. Edits are saved to an overrides file that the configuration loads after
/// appsettings.json, so they take precedence and survive deploys that replace appsettings.json. An edit is a partial
/// JSON object using the section's names. It's validated as a whole and written atomically, so an invalid edit changes
/// nothing and the configuration reload never sees a partial file.
/// </summary>
internal sealed class ConfigEditor(string appSettingsPath, string overridesPath)
{
    public const string SectionName = "CirculateWater";
    public const string OverridesFileName = "appsettings.overrides.json";

    private const string CerboIP = "CerboIP";
    private const string SensorVRMInstance = "SensorVRMInstance";
    private const string TempCheckFrequencySecs = "TempCheckFrequencySecs";
    private const string StagePrefix = "Stage";

    /// <summary>
    /// Allowed ranges for stage settings, which the app parses as integers.
    /// </summary>
    private static readonly Dictionary<string, (int Min, int Max)> StageRanges = new()
    {
        [nameof(TemperatureStage.TempThresholdF)] = (-60, 120),
        [nameof(TemperatureStage.CirculateFrequencyMins)] = (1, 1440),
        [nameof(TemperatureStage.CirculateDurationSecs)] = (1, 600),
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly object sync = new();

    /// <summary>
    /// Returns the settings in effect: appsettings.json with the overrides applied. Like the configuration, an invalid
    /// overrides file is ignored.
    /// </summary>
    public JsonObject ReadSection()
    {
        lock (sync)
        {
            return ReadEffectiveSection(TryReadOverrides(out var overridesRoot, out _) ? overridesRoot : new JsonObject());
        }
    }

    public ConfigEditResult Apply(string editJson)
    {
        lock (sync)
        {
            if (!TryReadOverrides(out var overridesRoot, out var overridesError))
            {
                // Saving would replace the file and lose whatever it holds, so leave fixing it to a person
                return new ConfigEditResult(false, [$"{OverridesFileName} is invalid and ignored; fix or delete it before making changes: {overridesError}"],
                    ReadEffectiveSection(new JsonObject()));
            }

            var current = ReadEffectiveSection(overridesRoot);

            if (string.IsNullOrWhiteSpace(editJson))
            {
                return new ConfigEditResult(false, ["Edit is empty"], current);
            }

            var updated = current.DeepClone().AsObject();
            var updatedOverridesRoot = overridesRoot.DeepClone().AsObject();
            var updatedOverrides = GetOrAddObject(updatedOverridesRoot, SectionName);
            var errors = new List<string>();
            try
            {
                if (JsonNode.Parse(editJson, documentOptions: ReadOptions) is not JsonObject edit || edit.Count == 0)
                {
                    return new ConfigEditResult(false, ["Edit must be a JSON object with at least one setting"], current);
                }

                foreach (var (name, value) in edit)
                {
                    ApplySetting(updated, updatedOverrides, name, value, errors);
                }
            }
            catch (JsonException ex)
            {
                return new ConfigEditResult(false, [$"Edit is not valid JSON: {ex.Message}"], current);
            }
            catch (ArgumentException ex)
            {
                // JsonObject reports a duplicate property name when it's first read
                return new ConfigEditResult(false, [$"Edit has a duplicate setting: {ex.Message}"], current);
            }

            if (errors.Count > 0)
            {
                return new ConfigEditResult(false, errors, current);
            }

            var json = updatedOverridesRoot.ToJsonString(WriteOptions);
            try
            {
                EnsureConfigurationCanLoad(json);
            }
            catch (Exception ex) when (IsInvalidJson(ex))
            {
                // Saving this would make the configuration ignore every override
                return new ConfigEditResult(false, [$"The change would make {OverridesFileName} invalid: {ex.GetBaseException().Message}"], current);
            }

            Write(overridesPath, json);
            return new ConfigEditResult(true, [], updated);
        }
    }

    /// <summary>
    /// Validates one setting and, when valid, sets it in both the settings in effect and the overrides.
    /// </summary>
    private static void ApplySetting(JsonObject effective, JsonObject overrides, string name, JsonNode value, List<string> errors)
    {
        if (name.Equals(CerboIP, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetString(value, out var host) && IsHost(host))
            {
                Set(effective, overrides, CerboIP, host);
            }
            else
            {
                errors.Add($"{CerboIP} must be an IPv4 address like 192.168.10.100, or a host name");
            }
        }
        else if (name.Equals(SensorVRMInstance, StringComparison.OrdinalIgnoreCase))
        {
            SetInt(effective, overrides, SensorVRMInstance, value, 0, 255, SensorVRMInstance, errors);
        }
        else if (name.Equals(TempCheckFrequencySecs, StringComparison.OrdinalIgnoreCase))
        {
            // Whole seconds avoid culture-dependent decimal parsing; the cap keeps stage frequencies meaningful
            SetInt(effective, overrides, TempCheckFrequencySecs, value, 5, 300, TempCheckFrequencySecs, errors);
        }
        else if (name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            ApplyStage(effective, overrides, name, value, errors);
        }
        else
        {
            errors.Add($"Unknown setting '{name}'");
        }
    }

    private static void ApplyStage(JsonObject effective, JsonObject overrides, string name, JsonNode value, List<string> errors)
    {
        var stageName = KeyIn(effective, name);
        if (effective[stageName] is not JsonObject stage)
        {
            errors.Add($"Unknown stage '{name}'; existing stages can be edited but not added");
            return;
        }

        if (value is not JsonObject stageEdit || stageEdit.Count == 0)
        {
            errors.Add($"{stageName} must be a JSON object with at least one setting");
            return;
        }

        var stageOverrides = GetOrAddObject(overrides, stageName);
        foreach (var (settingName, settingValue) in stageEdit)
        {
            var setting = StageRanges.Keys.FirstOrDefault(k => k.Equals(settingName, StringComparison.OrdinalIgnoreCase));
            if (setting == null)
            {
                errors.Add($"Unknown setting '{stageName}.{settingName}'");
                continue;
            }

            var (min, max) = StageRanges[setting];
            SetInt(stage, stageOverrides, setting, settingValue, min, max, $"{stageName}.{setting}", errors);
        }
    }

    private static void SetInt(JsonObject effective, JsonObject overrides, string key, JsonNode value, int min, int max, string label, List<string> errors)
    {
        if (TryGetInt(value, out var number) && number >= min && number <= max)
        {
            Set(effective, overrides, key, number);
        }
        else
        {
            errors.Add($"{label} must be a whole number from {min} to {max}");
        }
    }

    private static void Set(JsonObject effective, JsonObject overrides, string key, JsonNode value)
    {
        effective[KeyIn(effective, key)] = value;
        overrides[KeyIn(overrides, key)] = value.DeepClone();
    }

    private static bool IsHost(string host)
    {
        if (host.Contains(':'))
        {
            return Uri.CheckHostName(host) == UriHostNameType.IPv6;
        }

        if (host.Length > 0 && char.IsAsciiDigit(host[0]))
        {
            // Must be an IPv4 address written the usual way: .NET reads a leading zero as octal (192.168.010.100 is
            // 192.168.8.100), and a typo like 192.168.10.1a would otherwise pass as a host name
            return IPAddress.TryParse(host, out var address)
                && address.AddressFamily == AddressFamily.InterNetwork
                && address.ToString() == host;
        }

        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>
    /// Copies the overrides onto the target the way the configuration layers files: names match case-insensitively, and
    /// a plain value or null doesn't remove a section's settings.
    /// </summary>
    private static void Merge(JsonObject target, JsonObject overrides)
    {
        foreach (var (name, value) in overrides)
        {
            var key = KeyIn(target, name);
            if (target[key] is JsonObject targetChild)
            {
                if (value is JsonObject child)
                {
                    Merge(targetChild, child);
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// Returns the existing property name that matches case-insensitively, so an edit never adds a differently cased duplicate.
    /// </summary>
    private static string KeyIn(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;

    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        var key = KeyIn(parent, name);
        if (parent[key] is not JsonObject child)
        {
            child = new JsonObject();
            parent[key] = child;
        }

        return child;
    }

    private static bool TryGetInt(JsonNode node, out int number)
    {
        number = 0;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out number);
    }

    private static bool TryGetString(JsonNode node, out string text)
    {
        text = null;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out text);
    }

    private JsonObject ReadEffectiveSection(JsonObject overridesRoot)
    {
        var appSettings = Parse(File.ReadAllText(appSettingsPath), appSettingsPath);
        var effective = (appSettings[KeyIn(appSettings, SectionName)] as JsonObject
            ?? throw new InvalidOperationException($"{appSettingsPath} has no {SectionName} section")).DeepClone().AsObject();

        // The configuration combines sections whose names differ only in case, so apply every one
        foreach (var (name, value) in overridesRoot)
        {
            if (name.Equals(SectionName, StringComparison.OrdinalIgnoreCase) && value is JsonObject overrides)
            {
                Merge(effective, overrides);
            }
        }

        return effective;
    }

    /// <summary>
    /// Reads the overrides file; a missing file means no overrides. Returns false when the configuration would reject it.
    /// </summary>
    private bool TryReadOverrides(out JsonObject root, out string error)
    {
        root = new JsonObject();
        error = null;
        if (!File.Exists(overridesPath))
        {
            return true;
        }

        try
        {
            var json = File.ReadAllText(overridesPath);
            EnsureConfigurationCanLoad(json);
            var parsed = Parse(json, overridesPath);
            // Read every property now, so a duplicate name is reported here rather than while merging
            Materialize(parsed);
            root = parsed;
            return true;
        }
        catch (Exception ex) when (IsInvalidJson(ex))
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// Throws when the configuration would reject the JSON, such as names that differ only in case, so the editor and the
    /// app agree on whether the overrides apply.
    /// </summary>
    private static void EnsureConfigurationCanLoad(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static bool IsInvalidJson(Exception ex) =>
        ex is JsonException or FormatException or InvalidDataException or ArgumentException or InvalidOperationException;

    private static void Materialize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (_, child) in obj)
            {
                Materialize(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                Materialize(child);
            }
        }
    }

    private static JsonObject Parse(string json, string path) =>
        JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
            ?? throw new InvalidOperationException($"{path} does not contain a JSON object");

    private static void Write(string path, string json)
    {
        var tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));
            // Reach the disk before the rename, so a power cut can't leave an empty settings file
            stream.Flush(flushToDisk: true);
        }

        // Rename is atomic, so the configuration reload never reads a partial file
        File.Move(tempPath, path, overwrite: true);
    }
}
