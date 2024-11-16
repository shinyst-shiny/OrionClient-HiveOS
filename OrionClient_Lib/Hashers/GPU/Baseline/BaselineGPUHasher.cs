using DrillX;
using DrillX.Compiler;
using DrillX.Solver;
using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.GPU.Baseline
{
    public partial class BaselineGPUHasher : BaseGPUHasher
    {
        public override string Name => "Baseline";
        public override string Description => "Baseline GPU hashing implementation";

        public override Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> HashxKernel()
        {
            return Equihash;
        }

        public override Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> EquihashKernel()
        {
            return Hashx;
        }

        protected override List<Device> GetValidDevices(IEnumerable<Device> devices)
        {
            if (devices == null)
            {
                return new List<Device>();
            }

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU && x.MaxNumThreadsPerGroup >= 512).ToList();
        }

        public override KernelConfig GetHashXKernelConfig(Device device, int maxNonces)
        {
            int iterationCount = maxNonces * (ushort.MaxValue + 1);
            int groupSize = 512;

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override KernelConfig GetEquihashKernelConfig(Device device, int maxNonces)
        {
            int iterationCount = 128 * maxNonces;
            int groupSize = 128;

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }
    }
}
