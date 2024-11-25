using DrillX.Compiler;
using DrillX;
using DrillX.Solver;
using ILGPU;
using ILGPU.Backends.PTX;
using ILGPU.Backends;
using ILGPU.IR.Intrinsics;
using ILGPU.IR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ILGPU.Runtime.Cuda;

namespace OrionClientLib.Hashers.GPU.AMDBaseline
{
    public partial class OpenCLBaselineGPUHasher
    {
        private static int _offsetCounter = 0;

        private static void Hashx(ArrayView<Instruction> program, ArrayView<SipState> key, ArrayView<ulong> results)
        {
            var grid = Grid.GlobalIndex;
            var group = Group.Dimension;

            int index = (grid.X * group.Y + grid.Y);// % (ushort.MaxValue + 1);

            //var sMemory = SharedMemory.Allocate<Instruction>(512);
            var registers = LocalMemory.Allocate<ulong>(8);
            var idx = Group.IdxX;

            //Interop.WriteLine("{0}", idx);

            //var bInstruction = program.SubView(index / (ushort.MaxValue + 1) * 512).Cast<ulong>();
            //var uMemory = sMemory.Cast<ulong>();

            //for (int i = idx; i < 1024; i += Group.DimX)
            //{
            //    uMemory[i] = bInstruction[i];
            //}

            //Group.Barrier();

            var sharedProgram = SharedMemory.Allocate<int>(Instruction.ProgramSize).Cast<ulong>();
            var p = program.Cast<int>().SubView(index / (ushort.MaxValue + 1) * Instruction.ProgramSize, Instruction.ProgramSize).Cast<ulong>();

            for (int i = idx; i < Instruction.ProgramSize / 2; i += Group.DimX)
            {
                sharedProgram[i] = p[i];
            }

            Group.Barrier();

            results[index] = Emulate(sharedProgram.Cast<int>(), key.SubView(index / (ushort.MaxValue + 1)), (ulong)(index % (ushort.MaxValue + 1)), registers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Emulate(ArrayView<int> program, ArrayView<SipState> key, ulong input, ArrayView<ulong> sRegs)
        {
            //return InterpretFull(ref program[0], ref key[0], input);

            return Interpret(program, key[0], input, sRegs);
            //return InterpetCompiled(key.V0, key.V1, key.V2, key.V3, input); 
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Digest(ArrayView<ulong> registers, SipState key)
        {
            unchecked
            {
                SipState x = new SipState
                {
                    V0 = registers[0] + key.V0,
                    V1 = registers[1] + key.V1,
                    V2 = registers[2],
                    V3 = registers[3]
                };

                x.SipRound();

                SipState y = new SipState
                {
                    V0 = registers[4],
                    V1 = registers[5],
                    V2 = registers[6] + key.V2,
                    V3 = registers[7] + key.V3
                };

                y.SipRound();

                return x.V0 ^ y.V0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Interpret(ArrayView<int> program, SipState key, ulong input, ArrayView<ulong> registers)
        {
            registers = SipHash24Ctr(key, input, registers);
            bool allowBranch = true;

            for (int i = 0; i < 16; i++)
            {
                //ArrayView<int> startInstruction = program.SubView(i * Instruction.Size, Instruction.Size);
                ref int startInstruction = ref program.SubView(i * Instruction.Size, Instruction.Size)[0];

                //Multiply
                var instruction = LoadMultInstruction(ref startInstruction, 0 * 4);
                //Used to skip over some bytes
                LoadTargetInstruction();
                var futureInstruction = LoadMultInstruction(ref startInstruction, 2 * 4);

                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));


            target:

                //Multiply
                Store(registers, futureInstruction.Dst, LoadRegister(registers, futureInstruction.Src) * LoadRegister(registers, futureInstruction.Dst));

                //Basic Opt
                var basicInstruction = LoadBasicInstruction(ref startInstruction, 3 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 4 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 5 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 6 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 7 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 8 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 9 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 10 * 4);
                var highMulInstruction = LoadBasicInstruction(ref startInstruction, 11 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                #region High Multiply

                uint mulhResult;

                if (highMulInstruction.Type == (int)OpCode.UMulH)
                {
                    var hi = Mul64hi(LoadRegister(registers, highMulInstruction.Dst), LoadRegister(registers, highMulInstruction.Src));
                    Store(registers, highMulInstruction.Dst, hi);
                    mulhResult = (uint)hi;
                }
                else
                {
                    var hi = Mul64hi((long)LoadRegister(registers, highMulInstruction.Dst), (long)LoadRegister(registers, highMulInstruction.Src));
                    Store(registers, highMulInstruction.Dst, hi);
                    mulhResult = (uint)hi;
                }

                #endregion

                //Basic opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 12 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 13 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //if (i == 15)
                //{
                //    Interop.WriteLine("{8}, {9}, {10}, {11}\n{0}\n{1}\n{2}\n{3}\n{4}\n{5}\n{6}\n{7}\n", registers.V0, registers.V1, registers.V2, registers.V3, registers.V4, registers.V5, registers.V6, registers.V7, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Type, basicInstruction.Operand);
                //}

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 14 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 15 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 16 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                int branchOp = LoadBranchInstruction(ref startInstruction, 17 * 4).Mask;
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Branch

                if (allowBranch && (branchOp & mulhResult) == 0)
                {
                    allowBranch = false;

                    goto target;
                }

                Group.Barrier();

                //Multiply
                instruction = LoadMultInstruction(ref startInstruction, 18 * 4);
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 19 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 20 * 4);
                highMulInstruction = LoadBasicInstruction(ref startInstruction, 21 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                #region High Multiply

                if (highMulInstruction.Type == (int)OpCode.UMulH)
                {
                    var hi = Mul64hi(LoadRegister(registers, highMulInstruction.Dst), LoadRegister(registers, highMulInstruction.Src));
                    Store(registers, highMulInstruction.Dst, hi);
                }
                else
                {
                    var hi = Mul64hi((long)LoadRegister(registers, highMulInstruction.Dst), (long)LoadRegister(registers, highMulInstruction.Src));
                    Store(registers, highMulInstruction.Dst, hi);
                }

                #endregion

                //Basic opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 22 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 23 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 24 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 25 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 26 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 27 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 28 * 4);
                instruction = LoadMultInstruction(ref startInstruction, 29 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Multiply
                Store(registers, instruction.Dst, LoadRegister(registers, instruction.Src) * LoadRegister(registers, instruction.Dst));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 30 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

                //Basic Opt
                basicInstruction = LoadBasicInstruction(ref startInstruction, 31 * 4);
                Store(registers, basicInstruction.Dst, BasicOperation(basicInstruction.Type, basicInstruction.Dst, basicInstruction.Src, basicInstruction.Operand, registers));

            }

            return Digest(registers, key);
        }

        #region Basic Operation

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong BasicOperation(int type, int dstId, int srcId, int operand, ArrayView<ulong> registers)
        {
            ulong dst = LoadRegister(registers, dstId);

            if (type != (int)OpCode.Rotate)
            {
                ulong src = 0;

                LoadDualRegister(registers, srcId, ref src);

                if (type == (int)OpCode.AddShift)
                {
                    return Mad(dst, src, (ulong)operand);
                }

                return dst ^ src;
            }

            return dst.Ror(operand);
        }

        #endregion

        #region Loading Instruction

        [IntrinsicMethod(nameof(LoadInstruction_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe BasicInstruction LoadBasicInstruction(ref int startInstruction, int index)
        {
            fixed (int* ptr = &startInstruction)
            {
                return ((BasicInstruction*)(ptr + index))[0];
            }
        }

        private static void LoadInstruction_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var instructionRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.shared.v4.s32 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[0])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[1])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[2])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[3])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(instructionRef)}+{_offsetCounter}]");
            command.Dispose();

            _offsetCounter += 16;
        }


        [IntrinsicMethod(nameof(LoadBranchInstruction_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe BranchInstruction LoadBranchInstruction(ref int startInstruction, int index)
        {
            fixed (int* ptr = &startInstruction)
            {
                return ((BranchInstruction*)(ptr + index))[0];
            }
        }

        private static void LoadBranchInstruction_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var instructionRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.shared.s32 %{PTXRegisterAllocator.GetStringRepresentation(returnValue)}, [%{PTXRegisterAllocator.GetStringRepresentation(instructionRef)}+{_offsetCounter}]");
            command.Dispose();

            _offsetCounter += 16;
        }

        [IntrinsicMethod(nameof(LoadMultInstruction_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe MultIntruction LoadMultInstruction(ref int startInstruction, int index)
        {
            fixed (int* ptr = &startInstruction)
            {
                return ((MultIntruction*)(ptr + index))[0];
            }
        }

        private static void LoadMultInstruction_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var instructionRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.shared.v2.s32 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[0])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[1])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(instructionRef)}+{_offsetCounter}]");
            command.Dispose();

            _offsetCounter += 16;
        }

        [IntrinsicMethod(nameof(LoadTargetInstruction_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void LoadTargetInstruction()
        {
        }

        private static void LoadTargetInstruction_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            _offsetCounter += 16;
        }

        [IntrinsicMethod(nameof(LoadHighMultInstruction_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe HiMultInstruction LoadHighMultInstruction(ref int startInstruction, int index)
        {
            fixed (int* ptr = &startInstruction)
            {
                return ((HiMultInstruction*)(ptr + index))[0];
            }
        }

        private static void LoadHighMultInstruction_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var instructionRef = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);

            var returnValue = (RegisterAllocator<PTXRegisterKind>.CompoundRegister)codeGenerator.Allocate(value);
            var command = codeGenerator.BeginCommand($"ld.shared.v2.s32 {{" +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[0])}," +
                $"%{PTXRegisterAllocator.GetStringRepresentation((RegisterAllocator<PTXRegisterKind>.HardwareRegister)returnValue.Children[1])}" +
                $"}}, " +
                $"[%{PTXRegisterAllocator.GetStringRepresentation(instructionRef)}+{_offsetCounter}]");
            command.Dispose();

            _offsetCounter += 16;
        }

        #endregion

        #region Multiply Add

        [IntrinsicMethod(nameof(Mad_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mad(ulong a, ulong b, ulong operand)
        {
            return a + (b * operand);
        }

        private static void Mad_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var b = codeGenerator.LoadPrimitive(value[1]);
            var op = codeGenerator.LoadPrimitive(value[2]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"mad.lo.u64");
            command.AppendArgument(returnValue);
            command.AppendArgument(b);
            command.AppendArgument(op);
            command.AppendArgument(a);
            command.Dispose();
        }

        #endregion

        #region High Multiply 

        [IntrinsicMethod(nameof(Mulu64hi_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mul64hi(ulong a, ulong b)
        {
            uint num2 = (uint)a;
            uint num3 = (uint)(a >> 32);
            uint num4 = (uint)b;
            uint num5 = (uint)(b >> 32);
            ulong num6 = (ulong)num2 * (ulong)num4;
            ulong num7 = (ulong)((long)num3 * (long)num4) + (num6 >> 32);
            ulong num8 = (ulong)((long)num2 * (long)num5 + (uint)num7);
            return (ulong)((long)num3 * (long)num5 + (long)(num7 >> 32)) + (num8 >> 32);
        }

        [IntrinsicMethod(nameof(Muli64hi_Generate))]
        //[IntrinsicImplementation]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long Mul64hi(long a, long b)
        {
            ulong num = Mul64hi((ulong)a, (ulong)b);
            return (long)num - ((a >> 63) & b) - ((b >> 63) & a);
        }

        private static void Mulu64hi_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var b = codeGenerator.LoadPrimitive(value[1]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"mul.hi.u64");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        private static void Muli64hi_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var a = codeGenerator.LoadPrimitive(value[0]);
            var b = codeGenerator.LoadPrimitive(value[1]);
            var returnValue = codeGenerator.AllocateHardware(value);

            var command = codeGenerator.BeginCommand($"mul.hi.s64");
            command.AppendArgument(returnValue);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        #endregion

        #region SipHash

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ArrayView<ulong> SipHash24Ctr(SipState s, ulong input, ArrayView<ulong> ret)
        {
            s.V1 ^= 0xee;
            s.V3 ^= input;

            s.SipRound();
            s.SipRound();

            s.V0 ^= input;
            s.V2 ^= 0xee;

            s.SipRound();
            s.SipRound();
            s.SipRound();
            s.SipRound();

            ret[0] = s.V0;
            ret[1] = s.V1;
            ret[2] = s.V2;
            ret[3] = s.V3;

            s.V1 ^= 0xdd;

            s.SipRound();
            s.SipRound();
            s.SipRound();
            s.SipRound();

            ret[4] = s.V0;
            ret[5] = s.V1;
            ret[6] = s.V2;
            ret[7] = s.V3;

            return ret;
        }

        #endregion

        #region Load Register

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong LoadRegister(ArrayView<ulong> registers, int id)
        {
            return registers[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LoadDualRegister(ArrayView<ulong> registers, int id, ref ulong ret)
        {
            if ((uint)id >= 8)
            {
                ret = (ulong)id;
                return;
            }

            ret = registers[id];
        }

        #endregion

        #region Store Register

        private static unsafe void Store(ArrayView<ulong> registers, int id, long value)
        {
            registers[id] = (ulong)value;
        }

        private static unsafe void Store(ArrayView<ulong> registers, int id, ulong value)
        {
            registers[id] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //[IntrinsicMethod(nameof(Store_Generate))]
        [IntrinsicImplementation]
        private static unsafe void Store_Test(Registers* reg, int id, ulong value)
        {

        }

        private static void Store_Generate(PTXBackend backend, PTXCodeGenerator codeGenerator, Value value)
        {
            var registers = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[0]);
            var id = (RegisterAllocator<PTXRegisterKind>.HardwareRegister)codeGenerator.LoadPrimitive(value[1]);
            var ret = codeGenerator.AllocateHardware(value);

            ////Set predicate
            //var command = codeGenerator.BeginCommand($"setp.ls.u64 %{PTXRegisterAllocator.GetStringRepresentation(b)}, %{PTXRegisterAllocator.GetStringRepresentation(idd)}, 64");
            //command.Dispose();

            ////Move id into ret
            //command = codeGenerator.BeginCommand($"mov.b64 %{PTXRegisterAllocator.GetStringRepresentation(ret)}, %{PTXRegisterAllocator.GetStringRepresentation(id)}");
            //command.Dispose();

            //Add
            var command = codeGenerator.BeginCommand($"add.u64 %{PTXRegisterAllocator.GetStringRepresentation(ret)}, %{PTXRegisterAllocator.GetStringRepresentation(id)}, %{PTXRegisterAllocator.GetStringRepresentation(registers)}");
            command.Dispose();

            //Load from register
            command = codeGenerator.BeginCommand($"ld.local.b64 %{PTXRegisterAllocator.GetStringRepresentation(ret)}, [%{PTXRegisterAllocator.GetStringRepresentation(ret)}]");
            command.Dispose();
        }

        #endregion

        #region Store Key

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void StoreValues(ref ulong arr, ulong a, ulong b, ulong c, ulong d)
        {
            unsafe
            {
                fixed (ulong* v = &arr)
                {
                    v[0] = a;
                    v[1] = b;
                    v[2] = c;
                    v[3] = d;
                }
            }
        }

        #endregion

        struct Registers
        {
            public ulong V0;
            public ulong V1;
            public ulong V2;
            public ulong V3;
            public ulong V4;
            public ulong V5;
            public ulong V6;
            public ulong V7;
        }
    }
}
