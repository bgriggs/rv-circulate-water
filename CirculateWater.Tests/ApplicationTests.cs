using CirculateWater.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CirculateWater.Tests;

public class ApplicationTests : IDisposable
{
    /// <summary>
    /// A negative frequency makes the first matching reading circulate regardless of clock resolution.
    /// </summary>
    private const int CirculateImmediately = -1;

    private readonly CancellationTokenSource stop = new();
    private readonly FakeControlOutput output = new();
    private readonly ListLoggerProvider logs = new();
    private readonly StatusTracker status = new(TimeProvider.System);

    public void Dispose() => stop.Dispose();

    [Theory]
    [InlineData(28.0, 10)]
    [InlineData(6.0, 10)]
    [InlineData(5.0, 20)]
    [InlineData(-10.0, 20)]
    public async Task Circulates_UsingStageWithLowestThresholdAtOrAboveTemperature(double temperatureF, int expectedDurationSecs)
    {
        await RunAsync(CreateConfig(CirculateImmediately), () => temperatureF);

        Assert.Equal(TimeSpan.FromSeconds(expectedDurationSecs), Assert.Single(output.Circulations));
        Assert.Equal(0, output.EnsureClosedCalls);
        var snapshot = status.GetSnapshot();
        Assert.Equal(expectedDurationSecs, snapshot.LastCirculationDurationSecs);
        Assert.False(snapshot.SolenoidIsOpen);
    }

    [Fact]
    public async Task DoesNotCirculate_WhenTemperatureAboveAllStages()
    {
        await RunAsync(CreateConfig(CirculateImmediately), () => 28.1);

        Assert.Empty(output.Circulations);
        Assert.Equal(0, output.EnsureClosedCalls);
    }

    [Fact]
    public async Task DoesNotCirculate_BeforeFrequencyHasElapsed()
    {
        await RunAsync(CreateConfig(circulateFrequencyMins: 15), () => 20.0);

        Assert.Empty(output.Circulations);
    }

    [Fact]
    public async Task ReportsTemperatureAndActiveStage()
    {
        var snapshots = new ConcurrentQueue<StatusSnapshot>();
        status.Changed += () => snapshots.Enqueue(status.GetSnapshot());

        await RunAsync(CreateConfig(circulateFrequencyMins: 15), () => 20.0, () => 3.0, () => 50.0);

        Assert.Contains(snapshots, s => s.CurrentTemperatureF == 20.0 && s.ActiveStage == 1);
        Assert.Contains(snapshots, s => s.CurrentTemperatureF == 3.0 && s.ActiveStage == 2);
        Assert.Contains(snapshots, s => s.CurrentTemperatureF == 50.0 && s.ActiveStage == null);
    }

    [Fact]
    public async Task ReportsSolenoidOpen_OnlyWhileCirculating()
    {
        bool? openDuringCirculation = null;
        output.OnCirculate = (_, _) =>
        {
            openDuringCirculation = status.GetSnapshot().SolenoidIsOpen;
            return Task.CompletedTask;
        };

        await RunAsync(CreateConfig(CirculateImmediately), () => 20.0);

        Assert.True(openDuringCirculation);
        Assert.False(status.GetSnapshot().SolenoidIsOpen);
    }

    [Fact]
    public async Task ClosesSolenoid_WhenTemperatureUnavailable()
    {
        await RunAsync(CreateConfig(CirculateImmediately), () => null);

        Assert.Empty(output.Circulations);
        Assert.Equal(1, output.EnsureClosedCalls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("Temperature unavailable", status.GetSnapshot().LastError);
    }

    [Fact]
    public async Task ClosesSolenoidAndKeepsRunning_WhenTemperatureReadFails()
    {
        var failure = new IOException("Cerbo unreachable");

        var temperature = await RunAsync(CreateConfig(CirculateImmediately), () => throw failure, () => 20.0);

        Assert.Equal(2, temperature.ScriptedReads);
        Assert.Equal(1, output.EnsureClosedCalls);
        Assert.Single(output.Circulations);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception == failure);
        Assert.Equal("Cerbo unreachable", status.GetSnapshot().LastError);
    }

    [Fact]
    public async Task ClosesSolenoid_WhenCirculationFails()
    {
        output.OnCirculate = (_, _) => throw new InvalidOperationException("GPIO failure");

        await RunAsync(CreateConfig(CirculateImmediately), () => 20.0);

        Assert.Single(output.Circulations);
        Assert.Equal(1, output.EnsureClosedCalls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
        var snapshot = status.GetSnapshot();
        Assert.Equal("GPIO failure", snapshot.LastError);
        Assert.False(snapshot.SolenoidIsOpen);
    }

    [Fact]
    public async Task KeepsRunning_WhenClosingSolenoidFails()
    {
        output.OnEnsureClosed = () => throw new InvalidOperationException("GPIO failure");

        var temperature = await RunAsync(CreateConfig(CirculateImmediately), () => null, () => null);

        Assert.Equal(2, temperature.ScriptedReads);
        Assert.Equal(2, output.EnsureClosedCalls);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Error && e.Message == "Failed to close solenoid"));
        Assert.Equal("Failed to close solenoid: GPIO failure", status.GetSnapshot().LastError);
    }

    [Fact]
    public async Task ClosesSolenoidWithoutError_WhenStoppedDuringCirculation()
    {
        output.OnCirculate = (_, stoppingToken) =>
        {
            stop.Cancel();
            return Task.Delay(Timeout.Infinite, stoppingToken);
        };

        await RunAsync(CreateConfig(CirculateImmediately), () => 20.0);

        Assert.Single(output.Circulations);
        Assert.Equal(1, output.EnsureClosedCalls);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.False(status.GetSnapshot().SolenoidIsOpen);
    }

    [Fact]
    public async Task FindsStages_WhenSettingNamesUseDifferentCase()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["circulatewater:stage1:tempthresholdf"] = "28",
                ["circulatewater:stage1:circulatefrequencymins"] = CirculateImmediately.ToString(),
                ["circulatewater:stage1:circulatedurationsecs"] = "10",
                ["CirculateWater:TempCheckFrequencySecs"] = "0",
            })
            .Build();

        await RunAsync(config, () => 20.0);

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(output.Circulations));
    }

    [Fact]
    public async Task SkipsIncompleteStage_AndStillCirculatesForOthers()
    {
        await RunAsync(CreateConfigWithIncompleteStage2(), () => 20.0);

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(output.Circulations));
        Assert.Equal(0, output.EnsureClosedCalls);
        Assert.Equal("Stage2 settings are missing or invalid", status.GetSnapshot().LastError);
    }

    [Fact]
    public async Task ReportsLastingSettingsProblemOnce_SoNewerErrorsStayVisible()
    {
        await RunAsync(CreateConfigWithIncompleteStage2(), () => 20.0, () => throw new IOException("Cerbo unreachable"), () => 20.0);

        Assert.Equal("Cerbo unreachable", status.GetSnapshot().LastError);
        Assert.Single(logs.Entries, e => e.Message == "Stage2 settings are missing or invalid");
    }

    /// <summary>
    /// Stage 1 is complete; only Stage2's threshold is left, as after a deploy removed the rest of a stage that had an override.
    /// </summary>
    private static IConfiguration CreateConfigWithIncompleteStage2() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            ["CirculateWater:Stage1:TempThresholdF"] = "28",
            ["CirculateWater:Stage1:CirculateFrequencyMins"] = CirculateImmediately.ToString(),
            ["CirculateWater:Stage1:CirculateDurationSecs"] = "10",
            ["CirculateWater:Stage2:TempThresholdF"] = "5",
            ["CirculateWater:TempCheckFrequencySecs"] = "0",
        })
        .Build();

    /// <summary>
    /// Stage 1 covers 28F and below (10s); stage 2 covers 5F and below (20s). Temperature is checked continuously.
    /// </summary>
    private static IConfiguration CreateConfig(int circulateFrequencyMins) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            ["CirculateWater:Stage1:TempThresholdF"] = "28",
            ["CirculateWater:Stage1:CirculateFrequencyMins"] = circulateFrequencyMins.ToString(),
            ["CirculateWater:Stage1:CirculateDurationSecs"] = "10",
            ["CirculateWater:Stage2:TempThresholdF"] = "5",
            ["CirculateWater:Stage2:CirculateFrequencyMins"] = circulateFrequencyMins.ToString(),
            ["CirculateWater:Stage2:CirculateDurationSecs"] = "20",
            ["CirculateWater:TempCheckFrequencySecs"] = "0",
        })
        .Build();

    /// <summary>
    /// Runs the main loop over the scripted readings. The fake temperature stops the loop once they are used up.
    /// </summary>
    private async Task<FakeTemperature> RunAsync(IConfiguration config, params Func<double?>[] readings)
    {
        var temperature = new FakeTemperature(stop, readings);
        using var loggerFactory = new LoggerFactory([logs]);
        using var app = new Application(config, loggerFactory, output, temperature, status);

        await app.StartAsync(stop.Token);
        try
        {
            await app.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The loop ends by cancellation once the readings are used up
        }

        return temperature;
    }
}
