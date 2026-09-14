namespace CirculateWater;

internal interface IControlOutput
{
    public Task Circulate(TimeSpan duration, CancellationToken stoppingToken);

    /// <summary>
    /// Forces the output off so the solenoid is closed.
    /// </summary>
    public void EnsureClosed();
}
