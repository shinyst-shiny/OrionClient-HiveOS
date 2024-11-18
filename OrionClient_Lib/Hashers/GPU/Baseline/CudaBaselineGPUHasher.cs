using DrillX;
using DrillX.Compiler;
using DrillX.Solver;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.GPU.Baseline
{
    public partial class CudaBaselineGPUHasher : BaseGPUHasher
    {
        public override string Name => "Baseline";
        public override string Description => "Baseline GPU hashing for Nvidia GPUs";

        public override Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> HashxKernel()
        {
            _offsetCounter = 0;

            return Hashx;
        }

        public override Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> EquihashKernel()
        {
            return Equihash;
        }

        protected override List<Device> GetValidDevices(IEnumerable<Device> devices)
        {
            if (devices == null)
            {
                return new List<Device>();
            }

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU && x.MaxNumThreadsPerGroup >= 512 && x is CudaDevice).ToList();
        }

        public override KernelConfig GetHashXKernelConfig(Device device, int maxNonces)
        {
            int iterationCount = maxNonces * (ushort.MaxValue + 1);
            int groupSize = 16;

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
