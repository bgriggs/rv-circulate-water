using System.Device.Gpio;

namespace CirculateWater.Tests.Fakes;

/// <summary>
/// In-memory GPIO driver that records every value written to a pin.
/// </summary>
internal sealed class FakeGpioDriver : GpioDriver
{
    private readonly Dictionary<int, PinMode> modes = [];
    private readonly Dictionary<int, PinValue> values = [];

    public List<(int Pin, PinValue Value)> Writes { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>
    /// Invoked after each write, e.g. to cancel while the solenoid is open.
    /// </summary>
    public Action<PinValue> OnWrite { get; set; } = _ => { };

    public PinMode ModeOf(int pinNumber) => GetPinMode(pinNumber);

    public PinValue ValueOf(int pinNumber) => Read(pinNumber);

    protected override int PinCount => 28;

    protected override void OpenPin(int pinNumber)
    {
    }

    protected override void ClosePin(int pinNumber)
    {
    }

    protected override void SetPinMode(int pinNumber, PinMode mode) => modes[pinNumber] = mode;

    protected override PinMode GetPinMode(int pinNumber) => modes.GetValueOrDefault(pinNumber, PinMode.Input);

    protected override bool IsPinModeSupported(int pinNumber, PinMode mode) => true;

    protected override PinValue Read(int pinNumber) => values.GetValueOrDefault(pinNumber, PinValue.Low);

    protected override void Write(int pinNumber, PinValue value)
    {
        values[pinNumber] = value;
        Writes.Add((pinNumber, value));
        OnWrite(value);
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback) =>
        throw new NotSupportedException();

    protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
