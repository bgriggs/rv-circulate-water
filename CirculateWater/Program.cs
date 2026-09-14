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
        var config = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

        logger.Info("Starting...");

        var host = new HostBuilder()
           .ConfigureServices((builderContext, services) =>
           {
               //services.AddSingleton<ILogger>(logger);
               services.AddSingleton<IConfiguration>(config);
               services.AddTransient<IControlOutput, RpiControlOutput>();
               services.AddTransient<ITemperature, VictronModbusTemperatureSource>();
               services.AddLogging(loggingBuilder =>
               {
                   loggingBuilder.ClearProviders();
                   loggingBuilder.SetMinimumLevel(LogLevel.Debug);
                   loggingBuilder.AddNLog();
               });
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