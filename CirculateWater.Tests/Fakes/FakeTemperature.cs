namespace CirculateWater.Tests.Fakes;

/// <summary>
/// Returns scripted readings, then stops the application on the next read.
/// </summary>
internal sealed class FakeTemperature(CancellationTokenSource stop, params Func<double?>[] readings) : ITemperature
{
    /// <summary>
    /// Returned once the script is used up; above every stage, so the loop does nothing before stopping.
    /// </summary>
    public const double AboveAllStagesF = 100;

    private int index;

    public int ScriptedReads => index;

    public Task<double?> GetTemperatureF()
    {
        if (index >= readings.Length)
        {
            stop.Cancel();
            return Task.FromResult<double?>(AboveAllStagesF);
        }

        try
        {
            return Task.FromResult(readings[index++]());
        }
        catch (Exception ex)
        {
            return Task.FromException<double?>(ex);
        }
    }
}
