using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CirculateWater;

/// <summary>
/// Main processing loop checking for temperature.
/// </summary>
internal partial class Application : BackgroundService
{
    /// <summary>
    /// Used when TempCheckFrequencySecs is missing or invalid, so a bad setting can't stop the loop.
    /// </summary>
    private const double DefaultTempCheckFrequencySecs = 60;

    private readonly IControlOutput controlOutput;
    private readonly ITemperature temperature;
    private readonly StatusTracker status;

    // Settings problems already reported, so a lasting problem is logged once rather than on every loop
    private string reportedStageProblems;
    private string reportedFrequencyProblem;

    private IConfiguration Config { get; }
    private ILogger Logger { get; }

    public Application(IConfiguration config, ILoggerFactory loggerFactory, IControlOutput controlOutput, ITemperature temperature, StatusTracker status)
    {
        Config = config;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.controlOutput = controlOutput;
        this.temperature = temperature;
        this.status = status;
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
                status.RecordReading(tempF, stageSettings?.StageNumber);
                if (tempF == null)
                {
                    Logger.LogWarning("Temperature unavailable, keeping solenoid closed");
                    status.RecordError("Temperature unavailable");
                    EnsureClosed();
                }
                else if (stageSettings != null)
                {
                    Logger.LogDebug($"Stage {stageSettings.StageNumber} active: {tempF:0.0}F <= {stageSettings.TempThresholdF:0.0}F");

                    var elapsed = DateTime.UtcNow - lastCirc;
                    if (elapsed.TotalMinutes > stageSettings.CirculateFrequencyMins)
                    {
                        Logger.LogDebug($"Setting output ON for {stageSettings.CirculateDurationSecs}secs");
                        status.RecordCirculationStarted(stageSettings.CirculateDurationSecs);
                        await controlOutput.Circulate(TimeSpan.FromSeconds(stageSettings.CirculateDurationSecs), stoppingToken);
                        status.RecordSolenoidClosed();
                        Logger.LogDebug("Output off");
                        lastCirc = DateTime.UtcNow;
                    }
                    else
                    {
                        Logger.LogDebug($"Skipping circulation: Elapsed:{elapsed.TotalMinutes:0.#}mins CircDuration: {stageSettings.CirculateFrequencyMins}mins");
                    }
                }
                else
                {
                    Logger.LogDebug($"Current temperature {tempF:0.#}F is outside the range of available temp stages.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Logger.LogInformation("Stopping, closing solenoid");
                EnsureClosed();
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error in main loop, closing solenoid");
                status.RecordError(ex.Message);
                EnsureClosed();
            }

            Logger.LogDebug($"Processing complete in {sw.ElapsedMilliseconds:0.#}ms");
            await Task.Delay(TimeSpan.FromSeconds(GetTempCheckFrequencySecs()), stoppingToken);
        }
    }

    /// <summary>
    /// Forces the solenoid closed. Failures are logged rather than thrown so the loop keeps running.
    /// </summary>
    private void EnsureClosed()
    {
        try
        {
            controlOutput.EnsureClosed();
            status.RecordSolenoidClosed();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close solenoid");
            status.RecordError($"Failed to close solenoid: {ex.Message}");
        }
    }

    private double GetTempCheckFrequencySecs()
    {
        var value = Config["CirculateWater:TempCheckFrequencySecs"];
        var valid = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0 && secs <= 86400;
        ReportWhenChanged(ref reportedFrequencyProblem, valid ? null : $"Invalid TempCheckFrequencySecs '{value}', using {DefaultTempCheckFrequencySecs}s");
        return valid ? secs : DefaultTempCheckFrequencySecs;
    }

    /// <summary>
    /// Logs and records a settings problem when it appears or changes, so a lasting problem doesn't flood the log or keep
    /// replacing newer errors in the status.
    /// </summary>
    private void ReportWhenChanged(ref string reported, string problem)
    {
        problem = string.IsNullOrEmpty(problem) ? null : problem;
        if (problem == reported)
        {
            return;
        }

        reported = problem;
        if (problem != null)
        {
            Logger.LogError(problem);
            status.RecordError(problem);
        }
    }

    private TemperatureStage GetStage(double? temperatureF)
    {
        var configRoot = (ConfigurationRoot)Config;
        var items = configRoot.AsEnumerable().ToList();
        var stages = new List<TemperatureStage>();
        var problems = new List<string>();
        foreach (var item in items)
        {
            var m = Stage().Match(item.Key);
            if (m.Success)
            {
                try
                {
                    stages.Add(new TemperatureStage(int.Parse(m.Groups["sn"].Value), Config));
                }
                catch (Exception ex) when (ex is ArgumentNullException or FormatException or OverflowException)
                {
                    // Skip an incomplete or invalid stage rather than stopping circulation for every stage
                    problems.Add($"Stage{m.Groups["sn"].Value} settings are missing or invalid");
                }
            }
        }
        ReportWhenChanged(ref reportedStageProblems, string.Join("; ", problems));

        stages = [.. stages.OrderBy(s => s.TempThresholdF)];
        foreach (var s in stages)
        {
            if (temperatureF <= s.TempThresholdF)
            {
                return s;
            }
        }
        return null;
    }

    // Ignore case like the configuration does, since an overrides file may use different casing
    [GeneratedRegex(TemperatureStage.STAGE_PREFIX + "(?<sn>\\d+):TempThresholdF", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Stage();
}
