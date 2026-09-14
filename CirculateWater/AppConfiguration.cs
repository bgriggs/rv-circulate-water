using Microsoft.Extensions.Configuration;

namespace CirculateWater;

/// <summary>
/// Loads the app's settings: appsettings.json, then the settings changed over MQTT, which take precedence.
/// </summary>
internal static class AppConfiguration
{
    public const string AppSettingsFileName = "appsettings.json";

    /// <param name="overridesIgnored">True when the overrides file was invalid at startup and is being ignored.</param>
    public static ConfigurationRoot Build(string basePath, StatusTracker status, Action<Exception, string> logError, out bool overridesIgnored)
    {
        var loadFailed = false;
        var config = (ConfigurationRoot)new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(AppSettingsFileName, optional: true, reloadOnChange: true)
            // Deploys don't replace this file, so settings changed over MQTT survive them
            .AddJsonFile(source =>
            {
                source.Path = ConfigEditor.OverridesFileName;
                source.Optional = true;
                source.ReloadOnChange = true;
                // Skip an invalid file so the appsettings.json values apply, rather than failing to start
                source.OnLoadException = context =>
                {
                    var reason = context.Exception.GetBaseException().Message;
                    logError(context.Exception, $"Ignoring invalid {ConfigEditor.OverridesFileName}; appsettings.json values apply until it's fixed: {reason}");
                    status.RecordError($"{ConfigEditor.OverridesFileName} is invalid and ignored: {reason}");
                    loadFailed = true;
                    context.Ignore = true;
                };
            })
            .Build();

        overridesIgnored = loadFailed;
        return config;
    }
}
