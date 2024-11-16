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

namespace OrionClientLib.Hashers.GPU
{
    public class BaselineGPUHasher : BaseGPUHasher
    {
        public override string Name => "Baseline";

        public override string Description => "Baseline GPU hashing implementation";

        public override Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> HashxKernel()
        {
            return eq;
        }


        public override Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> EquihashKernel()
        {
            return eq2;
        }

        private static void eq2(ArrayView<ulong> values, ArrayView<EquixSolution> solutions, ArrayView<ushort> globalHeap, ArrayView<uint> solutionCount)
        {

        }

        private static void eq(ArrayView<Instruction> program, ArrayView<SipState> key, ArrayView<ulong> results)
        {

        }

        public override bool IsSupported()
        {
            try
            {
                using Context context = Context.Create(builder => builder.AllAccelerators());

                return context.Devices.Any(x => x.AcceleratorType != AcceleratorType.CPU);
            }
            catch
            {
                return false;
            }
        }

        public override KernelConfig GetHashXKernelConfig(Device device)
        {
            return default;
        }

        public override KernelConfig GetEquihashKernelConfig(Device device)
        {
            return default;
        }
    }
}
