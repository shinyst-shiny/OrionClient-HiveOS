using DrillX;
using DrillX.Compiler;
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

        public override void EquihashKernel(ArrayView<ulong> values, ArrayView<ushort> solutions, ArrayView<ushort> globalHeap, ArrayView<uint> solutionCount)
        {

        }

        public override void HashxKernel(ArrayView<Instruction> program, ArrayView<SipState> key, ArrayView<ulong> results)
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

        protected override void ExecuteThread(Tuple<int, int> range, ParallelLoopState loopState, ConcurrentQueue<Exception> exceptions)
        {

        }
    }
}
