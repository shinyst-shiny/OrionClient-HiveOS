using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DrillX.Compiler.Generator;

namespace DrillX.Compiler
{

    internal class Model
    {
        public const int NumRegisters = 8;
        public const int TargetCycles = 192;
        public const int MaxLatency = 4;
        public const int ScheduleSize = TargetCycles + MaxLatency;
        public const int NumExecutionPorts = 3;
        public const uint BranchMaskBitWeight = 4;
        public const uint RequiredInstructions = 512;
        public const uint RequiredOverallResultAtCycle = 194;
        public const uint RequiredMultiples = 192;

        public const RegisterId DisallowRegisterForAddShift = 5;

        const ExecPorts P5 = 1 << 0;
        const ExecPorts P0 = 1 << 1;
        const ExecPorts P1 = 1 << 2;
        const ExecPorts P01 = P0 | P1;
        const ExecPorts P05 = P0 | P5;
        const ExecPorts P015 = P0 | P01 | P5;

        internal static OpCode[] WideMulOpsTable = new OpCode[]
        {
            OpCode.SMulH,
            OpCode.UMulH,
        };

        internal static OpCode[] ImmediateSrcOpsTable = new OpCode[]
        {
            OpCode.Rotate,
            OpCode.XorConst,
            OpCode.AddConst,
            OpCode.AddConst
        };

        internal static OpCode[] NormalOpsTable = new OpCode[]
        {
            OpCode.Rotate,
            OpCode.XorConst,
            OpCode.AddConst,
            OpCode.AddConst,
            OpCode.Sub,
            OpCode.Xor,
            OpCode.XorConst,
            OpCode.AddShift,
        };

        internal static ulong InstructionLatencyCycles(OpCode code)
        {
            switch (code)
            {
                case OpCode.AddShift:
                case OpCode.AddConst:
                case OpCode.Sub:
                case OpCode.Xor:
                case OpCode.XorConst:
                case OpCode.Rotate:
                case OpCode.Target:
                case OpCode.Branch:
                    return 1;
                case OpCode.Mul:
                    return 3;
                case OpCode.UMulH:
                case OpCode.SMulH:
                    return 4;
            }

            throw new Exception("Invalid opcode");
        }

        internal static (ExecPorts execPort, ExecPorts? optionalExecPort) MicroOperations(OpCode opcode)
        {
            switch (opcode)
            {
                case OpCode.AddConst:
                    return (P015, null);
                case OpCode.Sub:
                    return (P015, null);
                case OpCode.Xor:
                    return (P015, null);
                case OpCode.Mul:
                    return (P1, null);
                case OpCode.UMulH:
                    return (P1, P5);
                case OpCode.SMulH:
                    return (P1, P5);
                case OpCode.AddShift:
                    return (P01, null);
                case OpCode.XorConst:
                    return (P015, null);
                case OpCode.Rotate:
                    return (P05, null);
                case OpCode.Target:
                    return (P015, P015);
                case OpCode.Branch:
                    return (P015, P015);
            }

            throw new Exception("Invalid opcode");
        }

        internal static OpcodeSelector ChooseOpCodeSelector(Pass pass, SubCycle subCycle)
        {
            var n = subCycle.Timestamp % 36;

            if (n == 1)
            {
                return OpcodeSelector.Target;
            }
            else if (n == 19)
            {
                return OpcodeSelector.Branch;
            }
            else if (n == 12 || n == 24)
            {
                return OpcodeSelector.WideMul;
            }
            else if (n % 3 == 0)
            {
                return OpcodeSelector.Mul;
            }
            else
            {
                switch (pass)
                {
                    case Pass.Original: return OpcodeSelector.Normal;
                    case Pass.Retry: return OpcodeSelector.ImmediateSrc;
                }
            }

            return OpcodeSelector.None;
        }

        internal static ulong InstructionSubCycleCount(OpCode opcode)
        {
            if (MicroOperations(opcode).optionalExecPort.HasValue)
            {
                return 2;
            }

            return 1;
        }

        internal static bool OpcodePairAllowed(OpCode lastSelectorOpCode, OpCode op)
        {
            if (lastSelectorOpCode == OpCode.None)
            {
                return true;
            }

            return !DisallowOpcodePair(lastSelectorOpCode, op);
        }

        private static bool DisallowOpcodePair(OpCode lastSelectorOpCode, OpCode op)
        {
            switch (op)
            {
                case OpCode.Mul:
                case OpCode.UMulH:
                case OpCode.SMulH:
                case OpCode.Target:
                case OpCode.Branch:
                    return false;
                case OpCode.AddConst:
                case OpCode.Xor:
                case OpCode.XorConst:
                case OpCode.Rotate:
                    return lastSelectorOpCode == op;
                case OpCode.AddShift:
                case OpCode.Sub:
                    return lastSelectorOpCode == OpCode.AddShift || lastSelectorOpCode == OpCode.Sub;
            }

            return false;
        }

        internal static bool DisallowSrcIsDst(OpCode op)
        {
            return op == OpCode.AddShift || op == OpCode.Mul || op == OpCode.Sub || op == OpCode.Xor;
        }

        internal static bool WriterPairAllowed(Pass pass, RegisterWriter lastWriter, RegisterWriter writerInfo)
        {
            if (lastWriter.Op == writerInfo.Op)
            {
                if (lastWriter.Op == RegisterWriterOp.Mul && pass == Pass.Original)
                {
                    return false;
                }
            }

            return lastWriter.Op != writerInfo.Op || lastWriter.Value != writerInfo.Value;
        }

        internal static Span<RegisterId?> SrcRegistersAllowed(Span<RegisterId?> available, int totalRegisters, OpCode op)
        {
            if (op == OpCode.AddShift && totalRegisters == 2)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (available[i] == Model.DisallowRegisterForAddShift)
                    {
                        return available.Slice(i, 1);
                    }
                }
            }

            return available;
        }
    }
}
