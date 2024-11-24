using DrillX.Compiler;
using DrillX.Solver;
using DrillX;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime;
using ILGPU;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OrionClientLib.Hashers.GPU.Baseline;

namespace OrionClientLib.Hashers.GPU.RTX4090Opt
{
    public partial class Cuda4090OptGPUHasher : BaseGPUHasher
    {
        public override string Name => "4090 Optimized";
        public override string Description => "Optimized implementation for Nvidia RTX 4090 GPUs";

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

        public override KernelConfig GetHashXKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int iterationCount = maxNonces * (ushort.MaxValue + 1);
            int groupSize = settings.GPUSetting.GPUBlockSize;

            var g = Math.Log2(groupSize);

            //4090 works best at 512
            if (device.Name.Contains("RTX 4090") || (int)g != g)
            {
                groupSize = 512;
            }

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override KernelConfig GetEquihashKernelConfig(Device device, int maxNonces, Settings settings)
        {
            int iterationCount = 128 * maxNonces;
            int groupSize = 128;

            return new KernelConfig(
                new Index3D((iterationCount + groupSize - 1) / groupSize, 1, 1),
                new Index3D(groupSize, 1, 1)
                );
        }

        public override CudaCacheConfiguration CudaCacheOption()
        {
            return CudaCacheConfiguration.PreferL1;
        }
    }
}
