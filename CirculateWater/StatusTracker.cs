namespace CirculateWater;

/// <summary>
/// Point-in-time status published for monitoring. Times are UTC.
/// </summary>
internal sealed record StatusSnapshot(
    DateTime AsOf,
    DateTime StartedAt,
    double? CurrentTemperatureF,
    DateTime? TemperatureReadAt,
    int? ActiveStage,
    bool SolenoidIsOpen,
    DateTime? LastCirculationAt,
    int? LastCirculationDurationSecs,
    string LastError,
    DateTime? LastErrorAt);

/// <summary>
/// Thread-safe store for the latest temperature, stage, solenoid state and error.
/// Written by <see cref="Application"/> and read by <see cref="MqttService"/>.
/// </summary>
internal sealed class StatusTracker
{
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private readonly DateTime startedAt;

    private double? currentTemperatureF;
    private DateTime? temperatureReadAt;
    private int? activeStage;
    private bool solenoidIsOpen;
    private DateTime? lastCirculationAt;
    private int? lastCirculationDurationSecs;
    private string lastError;
    private DateTime? lastErrorAt;

    public StatusTracker(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        startedAt = Now;
    }

    /// <summary>
    /// Raised after every update, outside the lock.
    /// </summary>
    public event Action Changed;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Records a temperature check. A null temperature means the reading was unavailable; the last read time is kept.
    /// </summary>
    public void RecordReading(double? temperatureF, int? stage)
    {
        Update(() =>
        {
            currentTemperatureF = temperatureF;
            activeStage = stage;
            if (temperatureF != null)
            {
                temperatureReadAt = Now;
            }
        });
    }

    public void RecordCirculationStarted(int durationSecs)
    {
        Update(() =>
        {
            solenoidIsOpen = true;
            lastCirculationAt = Now;
            lastCirculationDurationSecs = durationSecs;
        });
    }

    public void RecordSolenoidClosed() => Update(() => solenoidIsOpen = false);

    public void RecordError(string message)
    {
        Update(() =>
        {
            lastError = message;
            lastErrorAt = Now;
        });
    }

    public StatusSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new StatusSnapshot(Now, startedAt, currentTemperatureF, temperatureReadAt, activeStage, solenoidIsOpen,
                lastCirculationAt, lastCirculationDurationSecs, lastError, lastErrorAt);
        }
    }

    private void Update(Action change)
    {
        lock (sync)
        {
            change();
        }

        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Monitoring must never interrupt circulation
        }
    }
}
