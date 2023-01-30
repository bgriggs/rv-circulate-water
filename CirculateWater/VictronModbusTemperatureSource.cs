using Microsoft.Extensions.Configuration;
using NLog;
using NModbus;
using System.Net.Sockets;

namespace CirculateWater
{
    // https://www.victronenergy.com/live/ccgx:modbustcp_faq
    /// <summary>
    /// Connects to Victron Cerbo with Modbus.
    /// </summary>
    internal class VictronModbusTemperatureSource : ITemperature
    {
        private IConfiguration Config { get; }
        private ILogger Logger { get; }

        public VictronModbusTemperatureSource(IConfiguration config, ILogger logger)
        {
            Config = config;
            Logger = logger;
        }

        // Product ID	3300	uint16	1	0 to 65535	/ProductId	no	
        // Temperature scale factor	3301	uint16	100	0 to 655.35	/Scale no
        // Temperature offset	3302	int16	100	-327.68 to 327.67	/Offset no
        // Temperature type	3303	uint16	1	0 to 65535	/TemperatureType no	0=Battery;1=Fridge;2=Generic
        // Temperature	3304	int16	100	-327.68 to 327.67	/Temperature no  Degrees celsius
        // Temperature status	3305	uint16	1	0 to 65535	/Status no	0=OK;1=Disconnected;2=Short circuited;3=Reverse Polarity;4=Unknown
        // Not Working: Humidity	3306	uint16	10	0 to 6553.3	/Humidity no	%
        // Not Working: Sensor battery voltage	3307	uint16	100	0 to 655.35	/BatteryVoltage no  V
        // Not Working: Atmospheric pressure	3308	uint16	1	0 to 65535	/Pressure no  hPa
        public async Task<double?> GetTemperatureF()
        {
            using TcpClient client = new(Config["CirculateWater:CerboIP"], 502);
            var factory = new ModbusFactory();
            IModbusMaster master = factory.CreateMaster(client);

            ushort startAddress = 3300;
            ushort numInputs = 6;
            Logger.Debug($"Requesting registers from Cerbo {Config["CirculateWater:CerboIP"]}");
            var sensorId = byte.Parse(Config["CirculateWater:SensorVRMInstance"]);
            var registers = await master.ReadInputRegistersAsync(sensorId, startAddress, numInputs);

            var status = (Status)registers[5];
            if (status == Status.Ok)
            {
                double tempC = (short)registers[4] / 100.0;
                double tempF = (tempC * 1.8) + 32;
                Logger.Info($"Received temperature C={tempC:0.#} F={tempF:0.#} for VRM Instance {sensorId}");
                return tempF;
            }
            else
            {
                Logger.Error($"Failed to access Cerbo temperature. Device status is no OK: {status}.");
            }
            return null;
        }

        enum Status
        {
            Ok,
            Disconnected,
            ShortCircuit,
            ReversePolarity,
            Unknown
        }
    }
}
