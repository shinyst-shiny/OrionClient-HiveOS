using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DrillX.Compiler
{
    public class RuntimeProgram
    {
        public Instruction[] Instructions;

        public RuntimeProgram(Instruction[] instructions, SipRand rng)
        {
            Instructions = instructions;
        }

        public static bool TryGenerate(SipRand rng, out RuntimeProgram runtimeProgram)
        {
            Instruction[] instructions = new Instruction[512];

            Generator gen = new Generator(rng);

            bool success = gen.GenerateProgram(instructions);

            if (!success)
            {
                runtimeProgram = null;

                return false;
            }

            runtimeProgram = new RuntimeProgram(instructions, rng);

            return true;
        }

        public static bool TryGenerate(SipRand rng, Span<Instruction> instructions, out RuntimeProgram runtimeProgram)
        {
            Generator gen = new Generator(rng);

            bool success = gen.GenerateProgram(instructions);

            if (!success)
            {
                runtimeProgram = null;

                return false;
            }

            runtimeProgram = new RuntimeProgram(null, rng);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public ulong Emulate(SipState key, ulong input)
        {
            RegisterFile regs = new RegisterFile(key, input);

            Interpret(regs);

            return regs.Digest(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe ulong RunAsmCompiled(SipState key, ulong input, delegate* unmanaged[Cdecl]<ulong*, void> func, ulong* rrr)
        {
            ulong r0;
            ulong r1;
            ulong r2;
            ulong r3;
            ulong r4;
            ulong r5;
            ulong r6;
            ulong r7;

            #region SipHashCtr

            r0 = key.V0;
            r1 = key.V1 ^ 0xee;
            r2 = key.V2;
            r3 = key.V3 ^ input;

            SipRoundLow();
            SipRoundLow();

            r0 ^= input;
            r2 ^= 0xee;

            SipRoundLow();
            SipRoundLow();
            SipRoundLow();
            SipRoundLow();

            r4 = r0;
            r5 = r1 ^ 0xdd;
            r6 = r2;
            r7 = r3;

            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();
            SipRoundHigh();

            #endregion

            rrr[0] = r0;
            rrr[1] = r1;
            rrr[2] = r2;
            rrr[3] = r3;
            rrr[4] = r4;
            rrr[5] = r5;
            rrr[6] = r6;
            rrr[7] = r7;

            func(rrr);

            r0 = rrr[0];
            r1 = rrr[1];
            r2 = rrr[2];
            r3 = rrr[3];
            r4 = rrr[4];
            r5 = rrr[5];
            r6 = rrr[6];
            r7 = rrr[7];

            r0 += key.V0;
            r1 += key.V1;
            r6 += key.V2;
            r7 += key.V3;

            r0 += r1;
            r2 += r3;
            r3 = r3.Rol(16);
            r3 ^= r2;
            r0 = r0.Rol(32);
            r0 += r3;
            r4 += r5;
            r6 += r7;
            r7 = r7.Rol(16);
            r7 ^= r6;
            r4 = r4.Rol(32);
            r4 += r7;

            var r = r0 ^ r4;

            return r;

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            void SipRoundLow()
            {
                r0 += r1;
                r2 += r3;

                r1 = r1.Rol(13);
                r3 = r3.Rol(16);
                r1 ^= r0;
                r3 ^= r2;

                r0 = r0.Rol(32);

                r2 += r1;
                r0 += r3;
                r1 = r1.Rol(17);
                r3 = r3.Rol(21);
                r1 ^= r2;
                r3 ^= r0;
                r2 = r2.Rol(32);
            }


            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            void SipRoundHigh()
            {
                r4 += r5;
                r6 += r7;
                r5 = r5.Rol(13);
                r7 = r7.Rol(16);
                r5 ^= r4;
                r7 ^= r6;
                r4 = r4.Rol(32);

                r6 += r5;
                r4 += r7;
                r5 = r5.Rol(17);
                r7 = r7.Rol(21);
                r5 ^= r6;
                r7 ^= r4;
                r6 = r6.Rol(32);
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe void RunAsmCompiled_AVX2(SipState key, ulong startInput, delegate* unmanaged[Cdecl]<ulong*, void> func, ulong* rrr)
        {
            var output = rrr;
            rrr += 4;

            var vv0 = Vector256.Create(key.V0);
            var vv1 = Vector256.Create(key.V1);
            var vv2 = Vector256.Create(key.V2);
            var vv3 = Vector256.Create(key.V3);

            Vector256<ulong> s0 = vv0;
            Vector256<ulong> s1 = Avx2.Xor(vv1, Vector256.Create((ulong)0xee));
            Vector256<ulong> s2 = vv2;
            Vector256<ulong> input = Vector256.Create(startInput, startInput + 1, startInput + 2, startInput + 3);
            Vector256<ulong> s3 = Avx2.Xor(vv3, input);

            SipRound();
            SipRound();

            s0 = Avx2.Xor(s0, input);
            s2 = Avx2.Xor(s2, Vector256.Create((ulong)0xee));

            SipRound();
            SipRound();
            SipRound();
            SipRound();

            var b0 = Avx2.UnpackLow(s0, s1);
            var b1 = Avx2.UnpackLow(s2, s3);
            var b2 = Avx2.UnpackHigh(s0, s1);
            var b3 = Avx2.UnpackHigh(s2, s3);

            Avx2.Store(rrr, Avx2.Permute2x128(b0, b1, 0x20));
            Avx2.Store(rrr + 8, Avx2.Permute2x128(b2, b3, 0x20));
            Avx2.Store(rrr + 16, Avx2.Permute2x128(b0, b1, 0x31));
            Avx2.Store(rrr + 24, Avx2.Permute2x128(b2, b3, 0x31));

            s1 = Avx2.Xor(s1, Vector256.Create((ulong)0xdd));

            SipRound();
            SipRound();
            SipRound();
            SipRound();

            b0 = Avx2.UnpackLow(s0, s1);
            b1 = Avx2.UnpackLow(s2, s3);
            b2 = Avx2.UnpackHigh(s0, s1);
            b3 = Avx2.UnpackHigh(s2, s3);

            Avx2.Store(rrr + 4, Avx2.Permute2x128(b0, b1, 0x20));
            Avx2.Store(rrr + 12, Avx2.Permute2x128(b2, b3, 0x20));
            Avx2.Store(rrr + 20, Avx2.Permute2x128(b0, b1, 0x31));
            Avx2.Store(rrr + 28, Avx2.Permute2x128(b2, b3, 0x31));

            func(rrr);
            func(rrr + 8);
            func(rrr + 16);
            func(rrr + 24);

            s0 = Avx2.LoadVector256(rrr);
            s1 = Avx2.LoadVector256(rrr + 8);
            s2 = Avx2.LoadVector256(rrr + 16);
            s3 = Avx2.LoadVector256(rrr + 24);

            b0 = Avx2.UnpackLow(s0, s1);
            b1 = Avx2.UnpackLow(s2, s3);
            b2 = Avx2.UnpackHigh(s0, s1);
            b3 = Avx2.UnpackHigh(s2, s3);

            s0 = Avx2.Permute2x128(b0, b1, 0x20);
            s1 = Avx2.Permute2x128(b2, b3, 0x20);
            s2 = Avx2.Permute2x128(b0, b1, 0x31);
            s3 = Avx2.Permute2x128(b2, b3, 0x31);

            s0 = Avx2.Add(s0, Vector256.Create(key.V0)); //r0 += key.V0;
            s1 = Avx2.Add(s1, Vector256.Create(key.V1)); //r1 += key.V1;

            s0 = Avx2.Add(s0, s1);  //r0 += r1;
            s2 = Avx2.Add(s2, s3); //r2 += r3;
            s3 = Avx2.Shuffle(s3.AsSByte(), Vector256.Create(6, 7, 0, 1, 2, 3, 4, 5, 14, 15, 8, 9, 10, 11, 12, 13, 22, 23, 16, 17, 18, 19, 20, 21, 30, 31, 24, 25, 26, 27, 28, 29)).AsUInt64(); //r3 = r3.Rol(16);
            s3 = Avx2.Xor(s3, s2); //r3 ^= r2;
            s0 = Avx2.Shuffle(s0.AsInt32(), 177).AsUInt64(); //r0 = r0.Rol(32);
            s0 = Avx2.Add(s0, s3); //r0 += r3;

            var r0 = s0;

            s0 = Avx2.LoadVector256(rrr + 4);
            s1 = Avx2.LoadVector256(rrr + 12);
            s2 = Avx2.LoadVector256(rrr + 20);
            s3 = Avx2.LoadVector256(rrr + 28);

            b0 = Avx2.UnpackLow(s0, s1);
            b1 = Avx2.UnpackLow(s2, s3);
            b2 = Avx2.UnpackHigh(s0, s1);
            b3 = Avx2.UnpackHigh(s2, s3);

            s0 = Avx2.Permute2x128(b0, b1, 0x20);
            s1 = Avx2.Permute2x128(b2, b3, 0x20);
            s2 = Avx2.Permute2x128(b0, b1, 0x31);
            s3 = Avx2.Permute2x128(b2, b3, 0x31);

            s2 = Avx2.Add(s2, Vector256.Create(key.V2));// r6 += key.V2;
            s3 = Avx2.Add(s3, Vector256.Create(key.V3)); //r7 += key.V3;
            s0 = Avx2.Add(s0, s1); //r4 += r5;
            s2 = Avx2.Add(s2, s3); //r6 += r7;
            s3 = Avx2.Shuffle(s3.AsSByte(), Vector256.Create(6, 7, 0, 1, 2, 3, 4, 5, 14, 15, 8, 9, 10, 11, 12, 13, 22, 23, 16, 17, 18, 19, 20, 21, 30, 31, 24, 25, 26, 27, 28, 29)).AsUInt64();
            s3 = Avx2.Xor(s3, s2); //r7 ^= r6;
            s0 = Avx2.Shuffle(s0.AsInt32(), 177).AsUInt64();//r4 = r4.Rol(32);
            s0 = Avx2.Add(s0, s3); //r4 += r7;

            r0 = Avx2.Xor(r0, s0);

            Avx2.Store(output, r0);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRound()
            {
                s0 = Avx2.Add(s0, s1);
                s2 = Avx2.Add(s2, s3);
                s1 = Avx2.Or(Avx2.ShiftLeftLogical(s1, 13), Avx2.ShiftRightLogical(s1, (64 - 13)));

                s3 = Avx2.Shuffle(s3.AsSByte(), Vector256.Create(6, 7, 0, 1, 2, 3, 4, 5, 14, 15, 8, 9, 10, 11, 12, 13, 22, 23, 16, 17, 18, 19, 20, 21, 30, 31, 24, 25, 26, 27, 28, 29)).AsUInt64();
                s1 = Avx2.Xor(s1, s0);
                s3 = Avx2.Xor(s3, s2);
                s0 = Avx2.Shuffle(s0.AsInt32(), 177).AsUInt64();

                s2 = Avx2.Add(s2, s1);
                s0 = Avx2.Add(s0, s3);
                s1 = Avx2.Or(Avx2.ShiftLeftLogical(s1, 17), Avx2.ShiftRightLogical(s1, (64 - 17)));
                s3 = Avx2.Or(Avx2.ShiftLeftLogical(s3, 21), Avx2.ShiftRightLogical(s3, (64 - 21)));
                s1 = Avx2.Xor(s1, s2);
                s3 = Avx2.Xor(s3, s0);
                s2 = Avx2.Shuffle(s2.AsInt32(), 177).AsUInt64();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public unsafe void RunAsmCompiled_AVX512(SipState key, ulong startInput, delegate* unmanaged[Cdecl]<ulong*, void> func, ulong* rrr)
        {
            var output = rrr;
            rrr += 8;

            var vv0 = Vector512.Create(key.V0);
            var vv1 = Vector512.Create(key.V1);
            var vv2 = Vector512.Create(key.V2);
            var vv3 = Vector512.Create(key.V3);
            var ee = Vector512.Create((ulong)0xee);

            Vector512<ulong> s0 = vv0;
            Vector512<ulong> s1 = Avx512BW.Xor(vv1, ee);
            Vector512<ulong> s2 = vv2;
            Vector512<ulong> input = Vector512.Create(startInput, startInput + 1, startInput + 2, startInput + 3, startInput + 4, startInput + 5, startInput + 6, startInput + 7);
            Vector512<ulong> s3 = Avx512BW.Xor(vv3, input);

            SipRound();
            SipRound();

            s0 = Avx512BW.Xor(s0, input);
            s2 = Avx512BW.Xor(s2, ee);

            SipRound();
            SipRound();
            SipRound();
            SipRound();

            {
                var r0 = s0;
                var r1 = s1;
                var r2 = s2;
                var r3 = s3;

                s1 = Avx512BW.Xor(s1, Vector512.Create((ulong)0xdd));

                SipRound();
                SipRound();
                SipRound();
                SipRound();


                var a = Avx512DQ.UnpackLow(r0, r1);
                var b = Avx512DQ.UnpackLow(r2, r3);
                var c = Avx512DQ.UnpackLow(s0, s1);
                var d = Avx512DQ.UnpackLow(s2, s3);

                var tt = Vector512.Create(0ul, 1, 8, 9, 2, 3, 10, 11);

                var a_ = Avx512DQ.PermuteVar8x64x2(a, tt, b);
                var c_ = Avx512DQ.PermuteVar8x64x2(c, tt, d);

                tt = Vector512.Create(4ul, 5, 12, 13, 6, 7, 14, 15);
                var b_ = Avx512DQ.PermuteVar8x64x2(a, tt, b);
                var d_ = Avx512DQ.PermuteVar8x64x2(c, tt, d);

                tt = Vector512.Create(0ul, 1, 2, 3, 8, 9, 10, 11);
                var f0 = Avx512DQ.PermuteVar8x64x2(a_, tt, c_);
                var f4 = Avx512DQ.PermuteVar8x64x2(b_, tt, d_);

                tt = Vector512.Create(4ul, 5, 6, 7, 12, 13, 14, 15);
                var f2 = Avx512DQ.PermuteVar8x64x2(a_, tt, c_);
                var f6 = Avx512DQ.PermuteVar8x64x2(b_, tt, d_);

                Avx512BW.Store(rrr, f0);
                Avx512BW.Store(rrr + 16, f2);
                Avx512BW.Store(rrr + 32, f4);
                Avx512BW.Store(rrr + 48, f6);

                a = Avx512DQ.UnpackHigh(r0, r1);
                b = Avx512DQ.UnpackHigh(r2, r3);
                c = Avx512DQ.UnpackHigh(s0, s1);
                d = Avx512DQ.UnpackHigh(s2, s3);

                tt = Vector512.Create(0ul, 1, 8, 9, 2, 3, 10, 11);
                a_ = Avx512DQ.PermuteVar8x64x2(a, tt, b);
                c_ = Avx512DQ.PermuteVar8x64x2(c, tt, d);

                tt = Vector512.Create(4ul, 5, 12, 13, 6, 7, 14, 15);
                b_ = Avx512DQ.PermuteVar8x64x2(a, tt, b);
                d_ = Avx512DQ.PermuteVar8x64x2(c, tt, d);

                tt = Vector512.Create(0ul, 1, 2, 3, 8, 9, 10, 11);
                var f1 = Avx512DQ.PermuteVar8x64x2(a_, tt, c_);
                var f5 = Avx512DQ.PermuteVar8x64x2(b_, tt, d_);


                tt = Vector512.Create(4ul, 5, 6, 7, 12, 13, 14, 15);
                var f3 = Avx512DQ.PermuteVar8x64x2(a_, tt, c_);
                var f7 = Avx512DQ.PermuteVar8x64x2(b_, tt, d_);

                Avx512BW.Store(rrr + 8, f1);
                Avx512BW.Store(rrr + 24, f3);
                Avx512BW.Store(rrr + 40, f5);
                Avx512BW.Store(rrr + 56, f7);
            }

            for (int i = 0; i < 8; i++)
            {
                var temp = rrr + 8 * i;
                func(temp);

                var r0 = temp[0];
                var r1 = temp[1];
                var r2 = temp[2];
                var r3 = temp[3];
                var r4 = temp[4];
                var r5 = temp[5];
                var r6 = temp[6];
                var r7 = temp[7];

                r0 += key.V0;
                r1 += key.V1;
                r6 += key.V2;
                r7 += key.V3;

                r0 += r1;
                r0 = r0.Rol(32);
                r2 += r3;
                r3 = r3.Rol(16);
                r3 ^= r2;
                r0 += r3;

                r4 += r5;
                r6 += r7;
                r7 = r7.Rol(16);
                r7 ^= r6;
                r4 = r4.Rol(32);
                r4 += r7;

                output[i] = r0 ^ r4;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SipRound()
            {
                s0 = Avx512BW.Add(s0, s1);
                s2 = Avx512BW.Add(s2, s3);
                s1 = Avx512BW.RotateLeft(s1, 13);
                s3 = Avx512BW.RotateLeft(s3, 16);
                s1 = Avx512BW.Xor(s1, s0);
                s3 = Avx512BW.Xor(s3, s2);
                s0 = Avx512BW.RotateLeft(s0, 32);

                s2 = Avx512BW.Add(s2, s1);
                s0 = Avx512BW.Add(s0, s3);
                s1 = Avx512BW.RotateLeft(s1, 17);
                s3 = Avx512BW.RotateLeft(s3, 21);
                s1 = Avx512BW.Xor(s1, s2);
                s3 = Avx512BW.Xor(s3, s0);
                s2 = Avx512BW.RotateLeft(s2, 32);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Interpret(RegisterFile regs)
        {
            uint programCounter = 0;
            bool allowBranch = true;
            ulong? branchTarget = null;
            uint mulhResult = 0;

            while (programCounter < Instructions.Length)
            {
                var nextPc = programCounter + 1;

                var instruction = Instructions[programCounter];

                (RegisterId dst, RegisterId src, uint operand) = ((byte)instruction.Dst, (byte)instruction.Src, (uint)instruction.Operand);

                switch (instruction.Type)
                {
                    case OpCode.Target:
                        branchTarget = programCounter;
                        break;
                    case OpCode.Branch:
                        if (allowBranch && (operand & mulhResult) == 0)
                        {
                            allowBranch = false;

                            if (!branchTarget.HasValue)
                            {
                                throw new Exception("Generated programs always have a target before branch");
                            }

                            nextPc = (uint)branchTarget.Value;
                        }
                        break;
                    case OpCode.AddShift:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var b = regs.Load(src);

                            var r = a + (b << (int)operand);

                            regs.Store(dst, r);
                        }
                        break;
                    case OpCode.Rotate:
                        {
                            var a = regs.Load(dst);
                            var r = a.Ror((int)operand);

                            regs.Store(dst, r);
                        }
                        break;
                    case OpCode.AddConst:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var bSignExtended = (ulong)(int)operand;

                            regs.Store(dst, a + bSignExtended);
                        }
                        break;
                    case OpCode.Sub:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var b = regs.Load(src);

                            regs.Store(dst, a - b);
                        }
                        break;
                    case OpCode.Xor:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var b = regs.Load(src);
                            regs.Store(dst, a ^ b);
                        }
                        break;
                    case OpCode.XorConst:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var bSignExtended = (ulong)(int)operand;

                            regs.Store(dst, a ^ bSignExtended);
                        }
                        break;
                    case OpCode.Mul:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var b = regs.Load(src);

                            regs.Store(dst, a * b);
                        }
                        break;
                    case OpCode.UMulH:
                        unchecked
                        {
                            var a = regs.Load(dst);
                            var b = regs.Load(src);

                            var hi = Math.BigMul(a, b, out ulong r);
                            mulhResult = (uint)hi;

                            regs.Store(dst, hi);
                        }
                        break;
                    case OpCode.SMulH:
                        unchecked
                        {
                            var a = (long)regs.Load(dst);
                            var b = (long)regs.Load(src);

                            var hi = Math.BigMul(a, b, out long r);
                            mulhResult = (uint)hi;

                            regs.Store(dst, (ulong)hi);
                        }
                        break;
                }

                programCounter = nextPc;

                if(programCounter == 512)
                {

                }
            }
        }

        private struct RegisterFile
        {
            public ulong[] Registers;

            public RegisterFile(SipState key, ulong input)
            {
                Registers = SipRand.SipHash24Ctr(key, input);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Store(RegisterId id, ulong value)
            {
                Registers[id] = value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ulong Load(RegisterId id)
            {
                return Registers[id];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ulong Digest(SipState key)
            {
                unchecked
                {
                    SipState x = SipState.Create(
                            Registers[0] + key.V0,
                            Registers[1] + key.V1,
                            Registers[2],
                            Registers[3]
                        );

                    SipState y = SipState.Create(
                            Registers[4],
                            Registers[5],
                            Registers[6] + key.V2,
                            Registers[7] + key.V3
                        );

                    x.SipRound();
                    y.SipRound();

                    return x.V0 ^ y.V0;
                }
            }
        }
    }
}
