using CirculateWater.Tests.Fakes;

namespace CirculateWater.Tests;

public class StatusTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider time = new(Start);
    private readonly StatusTracker tracker;

    public StatusTrackerTests()
    {
        tracker = new StatusTracker(time);
    }

    [Fact]
    public void StartsClosedWithNoReadings()
    {
        var snapshot = tracker.GetSnapshot();

        Assert.Equal(Start.UtcDateTime, snapshot.StartedAt);
        Assert.False(snapshot.SolenoidIsOpen);
        Assert.Null(snapshot.CurrentTemperatureF);
        Assert.Null(snapshot.TemperatureReadAt);
        Assert.Null(snapshot.LastCirculationAt);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void RecordReading_KeepsLastReadTime_WhenTemperatureUnavailable()
    {
        tracker.RecordReading(20.5, 1);
        time.Now = Start.AddMinutes(1);
        tracker.RecordReading(null, null);

        var snapshot = tracker.GetSnapshot();
        Assert.Null(snapshot.CurrentTemperatureF);
        Assert.Null(snapshot.ActiveStage);
        Assert.Equal(Start.UtcDateTime, snapshot.TemperatureReadAt);
        Assert.Equal(Start.AddMinutes(1).UtcDateTime, snapshot.AsOf);
    }

    [Fact]
    public void TracksCirculation()
    {
        time.Now = Start.AddMinutes(5);
        tracker.RecordCirculationStarted(10);
        Assert.True(tracker.GetSnapshot().SolenoidIsOpen);

        tracker.RecordSolenoidClosed();

        var snapshot = tracker.GetSnapshot();
        Assert.False(snapshot.SolenoidIsOpen);
        Assert.Equal(Start.AddMinutes(5).UtcDateTime, snapshot.LastCirculationAt);
        Assert.Equal(10, snapshot.LastCirculationDurationSecs);
    }

    [Fact]
    public void RecordError_KeepsMessageAndTime()
    {
        time.Now = Start.AddMinutes(2);
        tracker.RecordError("Temperature unavailable");

        var snapshot = tracker.GetSnapshot();
        Assert.Equal("Temperature unavailable", snapshot.LastError);
        Assert.Equal(Start.AddMinutes(2).UtcDateTime, snapshot.LastErrorAt);
    }

    [Fact]
    public void RaisesChanged_OnEveryUpdate()
    {
        var changes = 0;
        tracker.Changed += () => changes++;

        tracker.RecordReading(20, null);
        tracker.RecordCirculationStarted(10);
        tracker.RecordSolenoidClosed();
        tracker.RecordError("failure");

        Assert.Equal(4, changes);
    }

    [Fact]
    public void KeepsRecording_WhenChangedHandlerThrows()
    {
        tracker.Changed += () => throw new InvalidOperationException("handler failure");

        tracker.RecordError("Temperature unavailable");

        Assert.Equal("Temperature unavailable", tracker.GetSnapshot().LastError);
    }
}
