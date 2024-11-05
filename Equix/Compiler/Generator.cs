using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DrillX.Compiler
{
    internal class Generator
    {
        public enum Pass { Original, Retry };
        public enum OpcodeSelector { None, Normal, ImmediateSrc, Mul, WideMul, Target, Branch };

        private RngBuffer _rng;
        private Scheduler _scheduler;
        private Validator _validator;

        private OpCode _lastSelectorOpCode = OpCode.None;

        public Generator(SipRand rng)
        {
            _rng = new RngBuffer(rng);
            _scheduler = new Scheduler();
            _validator = new Validator();
        }

        public bool GenerateProgram(Span<Instruction> instructions)
        {
            for (int i = 0; i < instructions.Length; i++)
            {
                //var a = _scheduler._cycle.Timestamp;
                //var b = _scheduler._subCycle.Timestamp;

                (Instruction instruction, RegisterWriter regw) = GenerateInstruction();

                //Console.WriteLine($"[{i+1}] {instruction.Dst} {instruction.Src} {instruction.Type} {(int)instruction.Operand} | {regw.Op} {regw.Value} -- Scheduler {a}. Sub: {b}");

                var stateAdvance = CommitInstructionData(instruction, regw);

                instructions[i] = instruction;

                if (!stateAdvance)
                {
                    break;
                }
            }

            return _validator.CheckWholeProgram(_scheduler, instructions);
        }

        private (Instruction instruction, RegisterWriter regw) GenerateInstruction()
        {
            while (true)
            {
                var result = InstructionGenAttempt(Pass.Original, out bool success);
                
                if (success)
                {
                    return result;
                }

                result = InstructionGenAttempt(Pass.Retry, out success);

                if (success)
                {
                    return result;
                }

                _scheduler.Stall();
            }
        }

        private bool CommitInstructionData(Instruction instruction, RegisterWriter regw)
        {
            _validator.CommitInstruction(instruction, regw);
            return _scheduler.AdvanceInstructionStream(instruction.Type);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (Instruction, RegisterWriter) InstructionGenAttempt(Pass pass, out bool success)
        {
            var op = ChooseOpCode(pass);
            var plan = _scheduler.InstructionPlan(op);
            (Instruction inst, RegisterWriter writer) = ChooseInstructionWithOpcodePlan(op, pass, ref plan, out success);

            if (success)
            {
                _scheduler.CommitInstructionPlan(plan, ref inst);
            }

            return (inst, writer);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (Instruction, RegisterWriter) ChooseInstructionWithOpcodePlan(OpCode op, Pass pass, ref InstructionPlan plan, out bool success)
        {
            success = true;

            switch (op)
            {
                case OpCode.Target:
                    return (new Instruction(OpCode.Target), new RegisterWriter(RegisterWriterOp.None));
                case OpCode.Branch:
                    return (new Instruction(OpCode.Branch, operand: SelectConstantWeightBitMask(Model.BranchMaskBitWeight)), new RegisterWriter(RegisterWriterOp.None));

                case OpCode.UMulH:
                case OpCode.SMulH:
                    {
                        var regW = new RegisterWriter(op == OpCode.UMulH ? RegisterWriterOp.UMulH : RegisterWriterOp.SMulH, _rng.NextU32());

                        (RegisterId src, RegisterId dst) = ChooseSrcDstRegsWithWriterInfo(op, pass, regW, plan, out success);

                        return (new Instruction(op, src, dst), regW);
                    }
                case OpCode.Mul:
                case OpCode.Sub:
                case OpCode.Xor:
                    {
                        var regW = new RegisterWriter(
                            op == OpCode.Mul ? RegisterWriterOp.Mul :
                                op == OpCode.Sub ? RegisterWriterOp.AddSub : RegisterWriterOp.Xor);

                        (RegisterId src, RegisterId dst, regW) = ChooseSrcDstRegs(op, pass, regW, ref plan, out success);

                        return (new Instruction(op, src, dst), regW);
                    }
                case OpCode.AddShift:
                    {
                        var regW = new RegisterWriter(RegisterWriterOp.AddSub);
                        var leftShift = _rng.NextU32() & 3;

                        (RegisterId src, RegisterId dst, regW) = ChooseSrcDstRegs(op, pass, regW, ref plan, out success);

                        return (new Instruction(op, src, dst, leftShift), regW);
                    }
                case OpCode.AddConst:
                case OpCode.XorConst:
                    {
                        var regW = new RegisterWriter(op == OpCode.AddConst ? RegisterWriterOp.AddConst : RegisterWriterOp.XorConst);

                        var src = SelectNonZeroU32(uint.MaxValue);
                        var dst = ChooseDstReg(op, pass, regW, null, ref plan, out success);

                        return (new Instruction(op, 0, dst, src), regW);
                    }
                case OpCode.Rotate:
                    {
                        var regW = new RegisterWriter(RegisterWriterOp.Rotate);

                        var src = SelectNonZeroU32(63);
                        var dst = ChooseDstReg(op, pass, regW, null, ref plan, out success);

                        return (new Instruction(op, 0, dst, src), regW);
                    }
            }

            return default;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (byte src, byte dst) ChooseSrcDstRegsWithWriterInfo(OpCode op, Pass pass, RegisterWriter writerInfo, InstructionPlan timingPlan, out bool success)
        {
            var src = ChooseSrcReg(op, ref timingPlan, out success);

            if (!success)
            {
                return default;
            }

            var dst = ChooseDstReg(op, pass, writerInfo, src, ref timingPlan, out success);

            return (src, dst);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private RegisterId ChooseSrcReg(OpCode op, ref InstructionPlan timingPlan, out bool success)
        {
            Span<RegisterId?> registerSet = stackalloc RegisterId?[Model.NumRegisters];

            int added = 0;

            for (int i = 0; i < Model.NumRegisters; i++)
            {
                RegisterId src = (RegisterId)i;

                if (_scheduler.RegisterAvailable(src, timingPlan.CycleIssued()))
                {
                    registerSet[added++] = src;
                }
            }

            registerSet = Model.SrcRegistersAllowed(registerSet, added, op);

            return SelectRegister(registerSet, added, out success);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (byte src, byte dst, RegisterWriter writerInfo) ChooseSrcDstRegs(OpCode op, Pass pass, RegisterWriter regW, ref InstructionPlan timingPlan, out bool success)
        {
            var src = ChooseSrcReg(op, ref timingPlan, out success);

            if (!success)
            {
                return default;
            }

            var writerInfo = new RegisterWriter(regW.Op, src);
            var dst = ChooseDstReg(op, pass, writerInfo, src, ref timingPlan, out success);

            return (src, dst, writerInfo);
        }

        private RegisterId ChooseDstReg(OpCode op, Pass pass, RegisterWriter writerInfo, RegisterId? src, ref InstructionPlan timingPlan, out bool success)
        {
            DstRegisterChecker validator = _validator.DstRegistersAllowed(op, pass, writerInfo, src);

            Span<RegisterId?> registerSet = stackalloc RegisterId?[Model.NumRegisters];
            int added = 0;

            for (int i = 0; i < Model.NumRegisters; i++)
            {
                RegisterId dst = (RegisterId)i;

                if (_scheduler.RegisterAvailable(dst, timingPlan.CycleIssued()) && validator.Check(dst))
                {
                    registerSet[added++] = dst;
                }
            }

            return SelectRegister(registerSet, added, out success);
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private RegisterId SelectRegister(Span<RegisterId?> regOptions, int regCount, out bool success)
        {
            success = true;

            switch (regCount)
            {
                case 0:
                    success = false;
                    return default;

                case 1:
                    return regOptions[0].Value;
                default:
                    var rrr = _rng.NextU32();
                    var r = (int)(rrr % regCount);
                    return regOptions[r].Value;
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint SelectNonZeroU32(uint mask)
        {
            while (true)
            {
                var result = _rng.NextU32() & mask;

                if (result != 0)
                {
                    return result;
                }
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OpCode ChooseOpCode(Pass pass)
        {
            OpCode op = OpCode.None;

            while (true)
            {
                var subCycle = _scheduler.InstructionStreamSubCycle();
                op = Apply(Model.ChooseOpCodeSelector(pass, subCycle));

                if (Model.OpcodePairAllowed(_lastSelectorOpCode, op))
                {
                    break;
                }
            }

            _lastSelectorOpCode = op;

            return op;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OpCode SelectOp(OpCode[] table, int tableSize)
        {
            var index = _rng.NextByte() & (tableSize - 1);

            return table[index];
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OpCode Apply(OpcodeSelector selector)
        {
            switch (selector)
            {
                case OpcodeSelector.Mul:
                    return OpCode.Mul;
                case OpcodeSelector.Target:
                    return OpCode.Target;
                case OpcodeSelector.Branch:
                    return OpCode.Branch;
                case OpcodeSelector.Normal:
                    return SelectOp(Model.NormalOpsTable, 8);
                case OpcodeSelector.ImmediateSrc:
                    return SelectOp(Model.ImmediateSrcOpsTable, 4);
                case OpcodeSelector.WideMul:
                    return SelectOp(Model.WideMulOpsTable, 2);
            }

            return OpCode.None;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint SelectConstantWeightBitMask(uint numOnes)
        {
            var result = 0u;
            var count = 0u;

            while (count < numOnes)
            {
                uint bit = (uint)(1 << (_rng.NextByte()) % 32);

                if ((result & bit) == 0)
                {
                    result |= bit;
                    count += 1;
                }
            }

            return result;
        }
    }
}
