using System.Device.Gpio;

namespace CirculateWater;

internal class RpiControlOutput : IControlOutput
{
    const int PIN = 26; // GPIO26 is pin 37 on RPi

    public async Task Circulate(TimeSpan duration, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        using GpioController controller = new();
        controller.OpenPin(PIN, PinMode.Output, PinValue.Low);
        try
        {
            controller.Write(PIN, PinValue.High);

            await Task.Delay(duration, stoppingToken);
        }
        finally
        {
            // Always close the solenoid, even when canceled or on error
            controller.Write(PIN, PinValue.Low);
        }
    }

    public void EnsureClosed()
    {
        using GpioController controller = new();
        controller.OpenPin(PIN, PinMode.Output, PinValue.Low);
        controller.Write(PIN, PinValue.Low);
    }
}
