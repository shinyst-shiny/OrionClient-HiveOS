using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers
{
    public interface IGPUHasher
    {
        public List<Device> GetDevices();
    }
}
