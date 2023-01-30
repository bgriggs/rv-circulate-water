using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NLog;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CirculateWater
{
    /// <summary>
    /// Main processing loop checking for temperature.
    /// </summary>
    internal partial class Application : BackgroundService
    {
        private readonly IControlOutput controlOutput;
        private readonly ITemperature temperature;

        private IConfiguration Config { get; }
        private ILogger Logger { get; }

        public Application(IConfiguration config, ILogger logger, IControlOutput controlOutput, ITemperature temperature)
        {
            Config = config;
            Logger = logger;
            this.controlOutput = controlOutput;
            this.temperature = temperature;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var lastCirc = DateTime.UtcNow;

            // Start loop for checking temperature
            while (!stoppingToken.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var tempF = await temperature.GetTemperatureF();
                    var stageSettings = GetStage(tempF);
                    if (stageSettings != null)
                    {
                        Logger.Debug($"Stage {stageSettings.StageNumber} active: {tempF:0.0}F <= {stageSettings.TempThresholdF:0.0}F");

                        var elapsed = DateTime.UtcNow - lastCirc;
                        if (elapsed.TotalMinutes > stageSettings.CirculateFrequencyMins)
                        {
                            Logger.Debug($"Setting output ON for {stageSettings.CirculateDurationSecs}secs");
                            await controlOutput.Circulate(TimeSpan.FromSeconds(stageSettings.CirculateDurationSecs), stoppingToken);
                            Logger.Debug("Output off");
                            lastCirc = DateTime.UtcNow;
                        }
                        else
                        {
                            Logger.Debug($"Skipping circulation: Elapsed:{elapsed.TotalMinutes:0.#}mins CircDuration: {stageSettings.CirculateFrequencyMins}mins");
                        }
                    }
                    else
                    {
                        Logger.Debug($"Current temperature {tempF:0.#}F is outside the range of available temp stages.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Error in main loop");
                }

                Logger.Debug($"Processing complete in {sw.ElapsedMilliseconds:0.#}ms");
                var frequency = TimeSpan.FromSeconds(double.Parse(Config["CirculateWater:TempCheckFrequencySecs"]));
                await Task.Delay(frequency, stoppingToken);
            }
        }

        private TemperatureStage GetStage(double? temperatureF)
        {
            var configRoot = (ConfigurationRoot)Config;
            var items = configRoot.AsEnumerable().ToList();
            var stages = new List<TemperatureStage>();
            foreach (var item in items)
            {
                var m = Stage().Match(item.Key);
                if (m.Success)
                {
                    var stageNumber = int.Parse(m.Groups["sn"].Value);
                    stages.Add(new TemperatureStage(stageNumber, Config));
                }
            }
            stages = stages.OrderBy(s => s.TempThresholdF).ToList();
            foreach (var s in stages)
            {
                if (temperatureF <= s.TempThresholdF)
                {
                    return s;
                }
            }
            return null;
        }

        [GeneratedRegex(TemperatureStage.STAGE_PREFIX + "(?<sn>\\d+):TempThresholdF")]
        private static partial Regex Stage();
    }
}
