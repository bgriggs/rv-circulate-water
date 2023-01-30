using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CirculateWater
{
    internal interface ITemperature
    {
        public Task<double?> GetTemperatureF();
    }
}
