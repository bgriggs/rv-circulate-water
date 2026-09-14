using CirculateWater.Tests.Fakes;
using System.Device.Gpio;

namespace CirculateWater.Tests;

public class RpiControlOutputTests
{
    private static readonly PinValue[] HighThenLow = [PinValue.Low, PinValue.High, PinValue.Low];

    private readonly FakeGpioDriver driver = new();
    private int controllersCreated;

    [Fact]
    public void EnsureClosed_DrivesPinLowAsOutput()
    {
        CreateOutput().EnsureClosed();

        Assert.NotEmpty(driver.Writes);
        Assert.All(driver.Writes, w => Assert.Equal(RpiControlOutput.PIN, w.Pin));
        Assert.DoesNotContain(PinValue.High, driver.Writes.Select(w => w.Value));
        Assert.Equal(PinMode.Output, driver.ModeOf(RpiControlOutput.PIN));
        Assert.Equal(PinValue.Low, driver.ValueOf(RpiControlOutput.PIN));
        Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task Circulate_OpensPinLowThenDrivesHighThenLow()
    {
        await CreateOutput().Circulate(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(HighThenLow, driver.Writes.Select(w => w.Value));
        Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task Circulate_DrivesPinLow_WhenCanceledWhileOpen()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        driver.OnWrite = value =>
        {
            if (value == PinValue.High)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateOutput().Circulate(TimeSpan.FromHours(1), cts.Token));

        Assert.Equal(HighThenLow, driver.Writes.Select(w => w.Value));
        Assert.Equal(PinValue.Low, driver.ValueOf(RpiControlOutput.PIN));
        Assert.True(driver.Disposed);
    }

    [Fact]
    public async Task Circulate_DoesNotTouchPin_WhenAlreadyCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateOutput().Circulate(TimeSpan.FromSeconds(10), cts.Token));

        Assert.Equal(0, controllersCreated);
        Assert.Empty(driver.Writes);
    }

    private RpiControlOutput CreateOutput() => new(() =>
    {
        controllersCreated++;
        return new GpioController(driver);
    });
}
