using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Hosting;
using NLog.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CirculateWater;

internal class Program
{
    async static Task Main()
    {
        var logger = LogManager.GetCurrentClassLogger();

        // Close the solenoid before anything else can fail, in case a previous run died while circulating
        try
        {
            new RpiControlOutput().EnsureClosed();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to close solenoid on startup");
        }

        var basePath = Directory.GetCurrentDirectory();
        var overridesPath = Path.Combine(basePath, ConfigEditor.OverridesFileName);
        var status = new StatusTracker(TimeProvider.System);
        var config = AppConfiguration.Build(basePath, status, (ex, message) => logger.Error(ex, message), out var overridesIgnored);

        logger.Info("Starting...");
        if (File.Exists(overridesPath) && !overridesIgnored)
        {
            logger.Info($"Settings in {ConfigEditor.OverridesFileName} take precedence over appsettings.json");
        }

        var host = new HostBuilder()
           .ConfigureServices((builderContext, services) =>
           {
               //services.AddSingleton<ILogger>(logger);
               services.AddSingleton<IConfiguration>(config);
               services.AddSingleton(status);
               services.AddSingleton(new ConfigEditor(Path.Combine(basePath, AppConfiguration.AppSettingsFileName), overridesPath));
               services.AddTransient<IControlOutput, RpiControlOutput>();
               services.AddTransient<ITemperature, VictronModbusTemperatureSource>();
               services.AddLogging(loggingBuilder =>
               {
                   loggingBuilder.ClearProviders();
                   loggingBuilder.SetMinimumLevel(LogLevel.Debug);
                   loggingBuilder.AddNLog();
               });
               // The host stops services in reverse order, so registering MQTT first lets Application close the solenoid first
               services.AddHostedService<MqttService>();
               services.AddHostedService<Application>();
           })
           .UseNLog()
           .Build();
        try
        {
            await host.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // suppress
        }
    }
}
