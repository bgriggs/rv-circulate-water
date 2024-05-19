namespace CirculateWater;

internal interface IControlOutput
{
    public Task Circulate(TimeSpan duration, CancellationToken stoppingToken);
}
