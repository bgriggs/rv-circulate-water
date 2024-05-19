namespace CirculateWater;

internal interface ITemperature
{
    public Task<double?> GetTemperatureF();
}
