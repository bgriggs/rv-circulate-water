using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;

namespace CirculateWater.Tests;

public class ConfigEditorTests : IDisposable
{
    private const string AppSettings = """
        {
          "CirculateWater": {
            "Stage1": { "TempThresholdF": 36, "CirculateFrequencyMins": 20, "CirculateDurationSecs": 10 },
            "Stage2": { "TempThresholdF": 18, "CirculateFrequencyMins": 10, "CirculateDurationSecs": 15 },
            "CerboIP": "192.168.10.100",
            "SensorVRMInstance": 34, // VRM Instance under the sensor name->Device in the Cerbo site
            "TempCheckFrequencySecs": 60
          },
          "Mqtt": { "Host": "localhost", "Port": 1883 }
        }
        """;

    private readonly string directory = Directory.CreateTempSubdirectory("circulate-config-").FullName;
    private readonly string appSettingsPath;
    private readonly string overridesPath;
    private readonly ConfigEditor editor;

    public ConfigEditorTests()
    {
        appSettingsPath = Path.Combine(directory, AppConfiguration.AppSettingsFileName);
        overridesPath = Path.Combine(directory, ConfigEditor.OverridesFileName);
        File.WriteAllText(appSettingsPath, AppSettings);
        editor = new ConfigEditor(appSettingsPath, overridesPath);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void ReadSection_ReturnsOnlyCirculateWaterSettings()
    {
        var section = editor.ReadSection();

        Assert.Equal(36, section["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.Equal("192.168.10.100", section["CerboIP"].GetValue<string>());
        Assert.Null(section["Mqtt"]);
    }

    [Fact]
    public void ReadSection_AppliesOverrides()
    {
        File.WriteAllText(overridesPath, """{ "CirculateWater": { "stage1": { "TempThresholdF": 30 }, "CerboIP": "192.168.10.101" } }""");

        var section = editor.ReadSection();

        var stage1 = section["Stage1"].AsObject();
        Assert.Equal(3, stage1.Count);
        Assert.Equal(30, stage1["TempThresholdF"].GetValue<int>());
        Assert.Equal(20, stage1["CirculateFrequencyMins"].GetValue<int>());
        Assert.Equal("192.168.10.101", section["CerboIP"].GetValue<string>());
        Assert.Equal(34, section["SensorVRMInstance"].GetValue<int>());
    }

    [Theory]
    [InlineData("""{ "CirculateWater": { "Stage1": null } }""")]
    [InlineData("""{ "CirculateWater": { "Stage1": 5 } }""")]
    [InlineData("""{ "CirculateWater": { "CerboIP": "192.168.10.101" }, "circulatewater": { "Stage1": { "TempThresholdF": 30 } } }""")]
    public void ReadSection_MatchesAppConfiguration_ForHandEditedOverrides(string overrides)
    {
        File.WriteAllText(overridesPath, overrides);

        var section = editor.ReadSection();

        using var config = LoadAppConfiguration();
        Assert.Equal(config["CirculateWater:Stage1:TempThresholdF"], section["Stage1"]["TempThresholdF"].GetValue<int>().ToString());
        Assert.Equal(config["CirculateWater:CerboIP"], section["CerboIP"].GetValue<string>());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "CirculateWater": { "CerboIP": "192.168.10.1", "CerboIP": "192.168.10.2" } }""")]
    [InlineData("""{ "CirculateWater": { "CerboIP": "192.168.10.1", "cerboip": "192.168.10.2" } }""")]
    public void ReadSection_IgnoresInvalidOverrides_LikeTheApp(string overrides)
    {
        File.WriteAllText(overridesPath, overrides);

        var section = editor.ReadSection();

        Assert.Equal("192.168.10.100", section["CerboIP"].GetValue<string>());
        Assert.Equal(36, section["Stage1"]["TempThresholdF"].GetValue<int>());
    }

    [Fact]
    public void Apply_RejectsEdits_WhileOverridesAreInvalid()
    {
        File.WriteAllText(overridesPath, "{ not json");

        var result = editor.Apply("""{ "Stage1": { "TempThresholdF": 34 } }""");

        Assert.False(result.Success);
        Assert.Contains(ConfigEditor.OverridesFileName, Assert.Single(result.Errors));
        Assert.Equal(36, result.Config["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.Equal("{ not json", File.ReadAllText(overridesPath));
    }

    [Fact]
    public void Apply_RejectsChange_ThatWouldMakeOverridesInvalid()
    {
        // Valid as it is, but saving Stage1 in the first section would duplicate a setting in the second
        const string Overrides = """{ "CirculateWater": { "CerboIP": "192.168.10.101" }, "circulatewater": { "Stage1": { "TempThresholdF": 30 } } }""";
        File.WriteAllText(overridesPath, Overrides);

        var result = editor.Apply("""{ "Stage1": { "TempThresholdF": 31 } }""");

        Assert.False(result.Success);
        Assert.Equal(Overrides, File.ReadAllText(overridesPath));
    }

    [Fact]
    public void Apply_SavesEditToOverrides_AndLeavesAppSettingsUntouched()
    {
        var result = editor.Apply("""{ "Stage1": { "TempThresholdF": 34 }, "TempCheckFrequencySecs": 30, "CerboIP": "cerbo.local" }""");

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(34, result.Config["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.Equal(20, result.Config["Stage1"]["CirculateFrequencyMins"].GetValue<int>());
        Assert.Equal(AppSettings, File.ReadAllText(appSettingsPath));
        Assert.False(File.Exists(overridesPath + ".tmp"));

        using var config = LoadAppConfiguration();
        Assert.Equal("34", config["CirculateWater:Stage1:TempThresholdF"]);
        Assert.Equal("20", config["CirculateWater:Stage1:CirculateFrequencyMins"]);
        Assert.Equal("18", config["CirculateWater:Stage2:TempThresholdF"]);
        Assert.Equal("30", config["CirculateWater:TempCheckFrequencySecs"]);
        Assert.Equal("cerbo.local", config["CirculateWater:CerboIP"]);
        Assert.Equal("localhost", config["Mqtt:Host"]);
    }

    [Fact]
    public void Apply_SavesOnlyEditedValues_AndKeepsEarlierEdits()
    {
        Assert.True(editor.Apply("""{ "Stage1": { "TempThresholdF": 34 } }""").Success);
        Assert.True(editor.Apply("""{ "Stage2": { "CirculateDurationSecs": 20 }, "CerboIP": "192.168.10.101" }""").Success);

        var overrides = JsonNode.Parse(File.ReadAllText(overridesPath))["CirculateWater"];
        Assert.Equal(34, overrides["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.Null(overrides["Stage1"]["CirculateFrequencyMins"]);
        Assert.Equal(20, overrides["Stage2"]["CirculateDurationSecs"].GetValue<int>());
        Assert.Equal("192.168.10.101", overrides["CerboIP"].GetValue<string>());
    }

    [Fact]
    public void Edits_SurviveReplacingAppSettings()
    {
        Assert.True(editor.Apply("""{ "Stage1": { "TempThresholdF": 34 } }""").Success);

        // A deploy replaces appsettings.json with the repo version
        File.WriteAllText(appSettingsPath, AppSettings.Replace("\"TempThresholdF\": 36", "\"TempThresholdF\": 28"));

        Assert.Equal(34, editor.ReadSection()["Stage1"]["TempThresholdF"].GetValue<int>());
        using var config = LoadAppConfiguration();
        Assert.Equal("34", config["CirculateWater:Stage1:TempThresholdF"]);
    }

    [Fact]
    public void Apply_MatchesNamesCaseInsensitively()
    {
        var result = editor.Apply("""{ "stage1": { "tempThresholdF": 30 } }""");

        Assert.True(result.Success);
        var stage1 = editor.ReadSection()["Stage1"].AsObject();
        Assert.Equal(3, stage1.Count);
        Assert.Equal(30, stage1["TempThresholdF"].GetValue<int>());
    }

    [Fact]
    public async Task ReloadsAppConfiguration_WhenFirstEditCreatesOverrides()
    {
        const string Key = "CirculateWater:Stage1:TempThresholdF";
        using var config = LoadAppConfiguration();

        Assert.True(editor.Apply("""{ "Stage1": { "TempThresholdF": 34 } }""").Success);

        // Wait for the value itself, since a reload of either file signals a change
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (config[Key] != "34")
        {
            var reloaded = new TaskCompletionSource();
            using var registration = config.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);
            if (config[Key] == "34")
            {
                break;
            }

            await reloaded.Task.WaitAsync(timeout.Token);
        }

        Assert.Equal("34", config[Key]);
    }

    [Theory]
    [InlineData("""{ "Stage1": { "TempThresholdF": 34 }, "Stage2": { "CirculateDurationSecs": 0 } }""")]
    [InlineData("""{ "Stage1": { "TempThresholdF": 34.5 } }""")]
    [InlineData("""{ "Stage1": { "TempThresholdF": "34" } }""")]
    [InlineData("""{ "Stage1": { "CirculateFrequencyMins": 0 } }""")]
    [InlineData("""{ "Stage1": { "CirculateDurationSecs": 601 } }""")]
    [InlineData("""{ "Stage1": { "Unknown": 1 } }""")]
    [InlineData("""{ "Stage1": 5 }""")]
    [InlineData("""{ "Stage3": { "TempThresholdF": 10 } }""")]
    [InlineData("""{ "Mqtt": { "Host": "elsewhere" } }""")]
    [InlineData("""{ "CerboIP": "" }""")]
    [InlineData("""{ "CerboIP": "192.168.10.1000" }""")]
    [InlineData("""{ "CerboIP": "192.168.10" }""")]
    [InlineData("""{ "CerboIP": "192.168.010.100" }""")]
    [InlineData("""{ "CerboIP": "192.168.10.1a" }""")]
    [InlineData("""{ "CerboIP": "1e1.1.1.1" }""")]
    [InlineData("""{ "SensorVRMInstance": 256 }""")]
    [InlineData("""{ "TempCheckFrequencySecs": 1 }""")]
    [InlineData("""{ "TempCheckFrequencySecs": 301 }""")]
    [InlineData("""{ "TempCheckFrequencySecs": 7.5 }""")]
    [InlineData("""{ "CerboIP": "192.168.10.1", "CerboIP": "192.168.10.2" }""")]
    [InlineData("""{ "Stage1": { "TempThresholdF": 30, "TempThresholdF": 31 } }""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Apply_RejectsWholeEdit_WhenAnythingIsInvalid(string edit)
    {
        var result = editor.Apply(edit);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(36, result.Config["Stage1"]["TempThresholdF"].GetValue<int>());
        Assert.False(File.Exists(overridesPath));
        Assert.Equal(AppSettings, File.ReadAllText(appSettingsPath));
    }

    /// <summary>
    /// Loads the settings the way the app does.
    /// </summary>
    private ConfigurationRoot LoadAppConfiguration() =>
        AppConfiguration.Build(directory, new StatusTracker(TimeProvider.System), (_, _) => { }, out _);
}
