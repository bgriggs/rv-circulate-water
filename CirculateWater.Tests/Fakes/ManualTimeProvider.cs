namespace CirculateWater.Tests.Fakes;

/// <summary>
/// Clock that only moves when a test sets it.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
