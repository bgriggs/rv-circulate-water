namespace CirculateWater.Tests.Fakes;

/// <summary>
/// Records calls instead of driving GPIO.
/// </summary>
internal sealed class FakeControlOutput : IControlOutput
{
    public List<TimeSpan> Circulations { get; } = [];

    public int EnsureClosedCalls { get; private set; }

    public Func<TimeSpan, CancellationToken, Task> OnCirculate { get; set; } = (_, _) => Task.CompletedTask;

    public Action OnEnsureClosed { get; set; } = () => { };

    public Task Circulate(TimeSpan duration, CancellationToken stoppingToken)
    {
        Circulations.Add(duration);
        return OnCirculate(duration, stoppingToken);
    }

    public void EnsureClosed()
    {
        EnsureClosedCalls++;
        OnEnsureClosed();
    }
}
