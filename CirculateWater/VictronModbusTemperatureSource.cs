using BigMission.VictronSdk.Modbus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CirculateWater;

// https://www.victronenergy.com/live/ccgx:modbustcp_faq
/// <summary>
/// Connects to Victron Cerbo with Modbus.
/// </summary>
internal class VictronModbusTemperatureSource : ITemperature
{
    private IConfiguration Config { get; }
    private ILogger Logger { get; }

    public VictronModbusTemperatureSource(IConfiguration config, ILoggerFactory loggerFactory)
    {
        Config = config;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }

    // Product ID	3300	uint16	1	0 to 65535	/ProductId	no	
    // Temperature scale factor	3301	uint16	100	0 to 655.35	/Scale no
    // Temperature offset	3302	int16	100	-327.68 to 327.67	/Offset no
    // Temperature type	3303	uint16	1	0 to 65535	/TemperatureType no	0=Battery;1=Fridge;2=Generic
    // Temperature	3304	int16	100	-327.68 to 327.67	/Temperature no  Degrees Celsius
    // Temperature status	3305	uint16	1	0 to 65535	/Status no	0=OK;1=Disconnected;2=Short circuited;3=Reverse Polarity;4=Unknown
    // Not Working: Humidity	3306	uint16	10	0 to 6553.3	/Humidity no	%
    // Not Working: Sensor battery voltage	3307	uint16	100	0 to 655.35	/BatteryVoltage no  V
    // Not Working: Atmospheric pressure	3308	uint16	1	0 to 65535	/Pressure no  hPa
    public async Task<double?> GetTemperatureF()
    {
        return await TemperatureSource.GetTemperatureF(Config["CirculateWater:CerboIP"], 502, byte.Parse(Config["CirculateWater:SensorVRMInstance"]), Logger);
    }
}
