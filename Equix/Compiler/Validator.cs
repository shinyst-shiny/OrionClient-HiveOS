
using System.ComponentModel;
using static DrillX.Compiler.Generator;

namespace DrillX.Compiler
{
    internal class Validator
    {
        public RegisterWriter[] WriterMap;
        public ulong MuliplyCount;

        public Validator()
        {
            WriterMap = new RegisterWriter[8];
            MuliplyCount = 0;
        }

        internal bool CheckWholeProgram(Scheduler scheduler, Span<Instruction> instructions)
        {
            return instructions.Length == Model.RequiredInstructions && 
                    scheduler.OverallLatency().Timestamp == Model.RequiredOverallResultAtCycle && 
                    MuliplyCount == Model.RequiredMultiples;
        }

        internal void CommitInstruction(Instruction instruction, RegisterWriter regw)
        {
            var op = instruction.Type;

            if(op == OpCode.Mul || op == OpCode.SMulH || op == OpCode.UMulH)
            {
                MuliplyCount++;
            }

            var dst = instruction.Destination();

            if (dst.HasValue)
            {
                WriterMap[dst.Value] = regw;
            }
            else
            {
                if(regw.Op != RegisterWriterOp.None)
                {
                    throw new Exception("Regw must be none");
                }
            }
        }

        internal DstRegisterChecker DstRegistersAllowed(OpCode op, Pass pass, RegisterWriter writerInfo, byte? src)
        {
            return new DstRegisterChecker(pass, WriterMap, writerInfo, op == OpCode.AddShift, Model.DisallowSrcIsDst(op) ? src : null);
        }
    }

    internal struct DstRegisterChecker
    {
        public Pass Pass;
        public RegisterWriter[] WriterMap;
        public RegisterWriter WriterInfo;
        public bool OpIsAddShift;
        public RegisterId? DisallowEqual;

        public DstRegisterChecker(Pass pass, RegisterWriter[] writerMap, RegisterWriter writerInfo, bool opIsAddShift, RegisterId? src)
        {
            Pass = pass;
            WriterMap = writerMap;
            WriterInfo = writerInfo;
            OpIsAddShift = opIsAddShift;
            DisallowEqual = src;
        }

        internal bool Check(RegisterId dst)
        {
            if(OpIsAddShift && dst == Model.DisallowRegisterForAddShift)
            {
                return false;
            }

            if (DisallowEqual.HasValue)
            {
                if(DisallowEqual.Value == dst)
                {
                    return false;
                }
            }

            return Model.WriterPairAllowed(Pass, WriterMap[dst], WriterInfo);
        }
    }
}