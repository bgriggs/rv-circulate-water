using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;

namespace CirculateWater
{
    internal class Program
    {
        private static Logger logger;

        async static Task Main()
        {
            logger = LogManager.GetCurrentClassLogger();

            var basePath = Directory.GetCurrentDirectory();
            var config = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();

            logger.Info("Starting...");

            var host = new HostBuilder()
               .ConfigureServices((builderContext, services) =>
               {
                   services.AddSingleton<ILogger>(logger);
                   services.AddSingleton<IConfiguration>(config);
                   services.AddTransient<IControlOutput, RpiControlOutput>();
                   services.AddTransient<ITemperature, VictronModbusTemperatureSource>();
                   services.AddHostedService<Application>();
               })
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
}