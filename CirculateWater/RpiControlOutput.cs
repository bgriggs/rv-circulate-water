using System.Device.Gpio;

namespace CirculateWater
{
    internal class RpiControlOutput : IControlOutput
    {
        const int PIN = 26; // GPIO26 is pin 37 on RPi

        public async Task Circulate(TimeSpan duration, CancellationToken stoppingToken)
        {
            using GpioController controller = new();
            controller.OpenPin(PIN, PinMode.Output);

            controller.Write(PIN, PinValue.High);
            
            await Task.Delay(duration, stoppingToken);

            controller.Write(PIN, PinValue.Low);
        }
    }
}
