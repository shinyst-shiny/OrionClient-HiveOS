﻿using Equix.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace DrillX.Compiler
{

    public unsafe class AVX512Compiler : BaseCompiler
    {
        private const int VectorSize = sizeof(ulong) * 8;
        private enum InstructionOpCode : int
        {
            Xor = 0xEF,
            ShiftRightArithmetic = 0x272,
            ShiftRightLogical = 0x173,
            ShiftLeftLogical = 0x373,
            RotateRight = 0x72,
            Mullo = 0x40,
            WideMul = 0xF4,
            And = 0xDB,
            Add = 0xD4,
            Sub = 0xFB,
            Mov = 0x6F,
            Blend = 0x64,
            Shuffle = 0x70
        }

        #region Regs
        private const byte zmm0 = 0;
        private const byte zmm1 = 1;
        private const byte zmm2 = 2;
        private const byte zmm3 = 3;
        private const byte zmm4 = 4;
        private const byte zmm5 = 5;
        private const byte zmm6 = 6;
        private const byte zmm7 = 7;
        private const byte zmm8 = 8;
        private const byte zmm9 = 9;
        private const byte zmm10 = 10;
        private const byte zmm11 = 11;
        private const byte zmm12 = 12;
        private const byte zmm13 = 13;
        private const byte zmm14 = 14;
        private const byte zmm15 = 15;
        private const byte zmm16 = 16;
        private const byte zmm17 = 17;
        private const byte zmm18 = 18;
        private const byte zmm19 = 19;
        private const byte zmm20 = 20;
        private const byte zmm21 = 21;
        private const byte zmm22 = 22;
        private const byte zmm23 = 23;
        private const byte zmm24 = 24;
        private const byte zmm25 = 25;
        private const byte zmm26 = 26;
        private const byte zmm27 = 27;
        private const byte zmm28 = 28;
        private const byte zmm29 = 29;
        private const byte zmm30 = 30;
        private const byte zmm31 = 31;
        #endregion

        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CompiledVectorHash(ulong* r);

        // AVX-512 prologue for Windows - preserves registers and loads vectors
        public readonly static byte[] avx512Prologue = new byte[]
        { 
            // Saves non-volatile registers
            0x48, 0x81, 0xEC, 0x00, 0x04, 0x00, 0x00,       // sub rsp, 1024
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x04, 0x24,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x4C, 0x24, 0x01,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x54, 0x24, 0x02,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x5C, 0x24, 0x03,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x64, 0x24, 0x04,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x6C, 0x24, 0x05,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x74, 0x24, 0x06,
            0x62, 0xE1, 0xFE, 0x48, 0x7F, 0x7C, 0x24, 0x07,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x44, 0x24, 0x08,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x4C, 0x24, 0x09,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x54, 0x24, 0x0A,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x5C, 0x24, 0x0B,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x64, 0x24, 0x0C,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x6C, 0x24, 0x0D,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x74, 0x24, 0x0E,
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x7C, 0x24, 0x0F
        };

        // AVX-512 epilogue for Windows - stores vectors and restores registers
        public readonly static byte[] avx512Epilogue = new byte[]
        {
            // Store data back into memory from non-volatile registers

            // Restore non-volatile registers
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x04, 0x24,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x4C, 0x24, 0x01,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x54, 0x24, 0x02,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x5C, 0x24, 0x03,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x64, 0x24, 0x04,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x6C, 0x24, 0x05,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x74, 0x24, 0x06,
            0x62, 0xE1, 0xFE, 0x48, 0x6F, 0x7C, 0x24, 0x07,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x44, 0x24, 0x08,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x4C, 0x24, 0x09,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x54, 0x24, 0x0A,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x5C, 0x24, 0x0B,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x64, 0x24, 0x0C,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x6C, 0x24, 0x0D,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x74, 0x24, 0x0E,
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x7C, 0x24, 0x0F,

            0x48, 0x81, 0xC4, 0x00, 0x04, 0x00, 0x00,       // add rsp, 128

            0xC3                             // ret
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitByte(byte* p, byte x) { *p = x; return ++p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitBytes(byte* p, params byte[] bytes)
        {
            Marshal.Copy(bytes, 0, (nint)p, bytes.Length);

            //foreach (byte b in bytes)
            //{
            //    *p++ = b;
            //}
            return p + bytes.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU16(byte* p, ushort x) { *(ushort*)p = x; p += 2; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU32(byte* p, uint x) { *(uint*)p = x; p += 4; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU64(byte* p, ulong x) { *(ulong*)p = x; p += 8; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitConstData(byte* p, ulong x)
        {
            *((ulong*)p + 0) = x;
            *((ulong*)p + 1) = x;
            *((ulong*)p + 2) = x;
            *((ulong*)p + 3) = x;
            *((ulong*)p + 4) = x;
            *((ulong*)p + 5) = x;
            *((ulong*)p + 6) = x;
            *((ulong*)p + 7) = x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EmitConstData(byte* p, ulong r0, ulong r1, ulong r2, ulong r3, ulong r4, ulong r5, ulong r6, ulong r7)
        {
            *((ulong*)p + 0) = r0;
            *((ulong*)p + 1) = r1;
            *((ulong*)p + 2) = r2;
            *((ulong*)p + 3) = r3;
            *((ulong*)p + 4) = r4;
            *((ulong*)p + 5) = r5;
            *((ulong*)p + 6) = r6;
            *((ulong*)p + 7) = r7;
        }

        public static delegate* unmanaged[Cdecl]<ulong*, void> HashCompileAVX512(Span<Instruction> instructions, byte* code)
        {
            #region Constant data

            byte* constDataStart = code + CodeSize;
            byte* constDataEnd = constDataStart;

            for (int i = instructions.Length - 1; i >= 0; i--)
            {
                var instruction = instructions[i];

                if (instruction.Type == OpCode.AddConst || instruction.Type == OpCode.XorConst || instruction.Type == OpCode.Branch)
                {
                    constDataStart -= VectorSize;

                    //Emit 8 each
                    EmitConstData(constDataStart, (ulong)instruction.Operand);
                }

                //lastSave[instruction.Dst] = i;
                //lastSave[instruction.Src] = -1;
            }

            constDataStart -= VectorSize;
            EmitConstData(constDataStart, 0xdd);
            constDataStart -= VectorSize;
            EmitConstData(constDataStart, 0xee);
            constDataStart -= VectorSize;
            EmitConstData(constDataStart, 0x8);
            constDataStart -= VectorSize;
            EmitConstData(constDataStart, 0, 1, 2, 3, 4, 5, 6, 7);
            #endregion

            byte* codeStart = code;
            byte* constDataLoc = constDataStart;

            #region Prologue

            if (OperatingSystem.IsLinux())
            {
                // In Linux, first argument is in rdi, move it to rcx for consistency
                code = EmitBytes(code, 0x48, 0x89, 0xF9);
            }

            code = EmitBytes(code, avx512Prologue);


            //Set keys to zmm26-zmm29
            /*
                vpbroadcastq zmm26, [rcx]
                vpbroadcastq zmm27, [rcx + 8]
                vpbroadcastq zmm28, [rcx + 16]
                vpbroadcastq zmm29, [rcx + 24]
            */
            code = EmitBytes(code,
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x11,
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x59, 0x01,
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x61, 0x02,
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x69, 0x03);

            //zmm31 will be inital input
            code = EmitInstruction(code, InstructionOpCode.Xor, zmm31, zmm31, zmm31); //Set zmm31 to 0
            code = EmitRipLoadInstruction(code, InstructionOpCode.Add, zmm31, zmm31, constDataLoc); //Add 0, 1, 2, 3, 4, 5, 6, 7
            constDataLoc += VectorSize;

            //zmm25 will be 8, 8, 8, 8, 8, 8, 8, 8
            code = EmitBytes(code, 0x62, 0x61, 0xFE, 0x48, 0x6F, 0x0D); //vmovdqu64 zmm25, [rip + ...]
            code = EmitU32(code, (uint)(constDataLoc - code - 4));
            constDataLoc += VectorSize;


            //Set 0xFFFFFFFF and 0x00000000 into registers for later
            code = EmitBytes(code, 0x62, 0x03, 0x3D, 0x40, 0x25, 0xC0, 0xFF); //vpternlogd zmm24,zmm24,zmm24,0xff -- Sets to all 1s
            code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm24, zmm24, 0x20);

            //Set ee into zmm30
            code = EmitBytes(code, 0x62, 0x61, 0xFE, 0x48, 0x6F, 0x35); //vmovdqu64 zmm30, [rip + ...]
            code = EmitU32(code, (uint)(constDataLoc - code - 4));
            constDataLoc += VectorSize;

            //Set loop counter to 0
            code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00); //mov r8, 0

            //Jump location
            var loopStart = code;

            code = EmitBytes(code, 0xC5, 0xF4, 0x46, 0xC9); //Set k1 to 1s
            code = EmitBytes(code, 0xC5, 0xEC, 0x46, 0xD2); //Set k2 to 1s


            #endregion

            code = Start(code, constDataLoc);

            constDataLoc += VectorSize;

            Debug.Assert(instructions.Length == 512);

            #region Hashing Function

            int isBreak = 0;
            byte maskRegister = 0;
            byte* targetStart = default;
            byte* targetConstDataLoc = default;
            byte* branchStartLoc = null;
            int* lastSave = stackalloc int[8];
            int* initialSave = stackalloc int[8];
            Span<int> lastSaveSpan = new Span<int>(lastSave, 8);
            Span<int> initialSaveSpan = new Span<int>(initialSave, 8);
            bool isBranched = false;

            for (int i = 0; i < instructions.Length; i++)
            {
                var instruction = instructions[i];

                byte dst = (byte)instruction.Dst;
                byte src = (byte)instruction.Src;
                byte finalDst = dst; //Used to skip over restoring the temp registers after a branch by setting it directly

                maskRegister = 0;

                if (isBranched)
                {
                    //Checks if we can save into original dst or not
                    if (lastSave[dst] == i)
                    {
                        maskRegister = 2;
                    }
                    else
                    {
                        finalDst += 16;
                    }

                    int iSaveSrc = initialSave[src];
                    int iSaveDst = initialSave[dst];

                    if (iSaveSrc < i)
                    {
                        src += 16;
                    }

                    if (iSaveDst < i)
                    {
                        dst += 16;
                    }
                }
                else if (instruction.Type != OpCode.Branch && targetStart != default)
                {
                    lastSave[src] = -1;
                    lastSave[dst] = (i);

                    if (initialSaveSpan[dst] == 0)
                    {
                        initialSaveSpan[dst] = i;
                    }
                }

                switch (instruction.Type)
                {
                    case OpCode.Target:
                        maskRegister = 0;
                        targetStart = code;
                        targetConstDataLoc = constDataLoc;
                        lastSaveSpan.Fill(-1);
                        initialSaveSpan.Fill(0x0);

                        break;
                    case OpCode.Branch:
                        if (targetStart != default)
                        {
                            //First time encountering branch
                            targetStart = default;

                            //Get branch mask
                            code = EmitRipLoadInstruction(code, InstructionOpCode.And, zmm8, zmm15, constDataLoc);

                            code = EmitBytes(code, 0x62, 0xD2, 0xBE, 0x48, 0x27, 0xD0); //vptestnmq k2,zmm8,zmm8 //Detects which values need to be branched (set to 1)
                            code = EmitBytes(code, 0xC5, 0xED, 0x41, 0xD1); //kandb  k2,k2,k1 //Remove any that have already branched
                            code = EmitBytes(code, 0xC5, 0xF9, 0x98, 0xD2); //kortestb k2, k2 //Check if we can fully skip this branch
                            code = EmitBytes(code, 0x0F, 0x84); //je
                            code = EmitU32(code, 0); //Skip current instruction (4 bytes), will be set later

                            branchStartLoc = code;

                            //Store registers
                            for (int z = 0; z < 8; z++)
                            {
                                if (initialSave[z] == 0)
                                {
                                    code = EmitInstruction(code, InstructionOpCode.Mov, z + 16, 0, z);
                                }
                            }

                            //Go back to target + 1
                            i -= 16;
                            constDataLoc = targetConstDataLoc;
                            isBranched = true;
                        }
                        else
                        {
                            constDataLoc += VectorSize;

                            //Restore registers
                            for (int z = 0; z < 8; z++)
                            {
                                if ((lastSave[z]) == -1)
                                {
                                    code = EmitBlend(code, z, z, z + 16, mask: 2);
                                }
                            }

                            code = EmitBytes(code, 0xC5, 0xED, 0x42, 0xC9); //kandnb k1, k2, k1

                            EmitU32((branchStartLoc - 4), (uint)(code - branchStartLoc)); //Properly set jmp target
                            isBranched = false;
                            //isBreak++;
                        }
                        break;
                    case OpCode.UMulH:

                        code = EmitShuffle(code, zmm9, dst, 0xb1);
                        code = EmitShuffle(code, zmm10, src, 0xb1);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm11, dst, src);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm12, zmm9, zmm10);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm14, src, zmm9);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm13, dst, zmm10);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm9, zmm11, 0x20);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm14, zmm9, zmm14);
                        code = EmitInstruction(code, InstructionOpCode.And, zmm9, zmm14, zmm24);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm13, zmm9, zmm13);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm13, zmm13, 0x20);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm14, zmm14, 0x20);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm13, zmm13, zmm14);
                        code = EmitInstruction(code, InstructionOpCode.Add, finalDst, zmm13, zmm12, mask: maskRegister);


                        if (targetStart != default)
                        {
                            //mulHiResult = umulhi(src, dst) & 0xFFFFFFFF
                            code = EmitInstruction(code, InstructionOpCode.And, zmm15, finalDst, zmm24);
                        }
                        break;
                    case OpCode.SMulH:

                        code = EmitShuffle(code, zmm9, dst, 0xb1);
                        code = EmitShuffle(code, zmm10, src, 0xb1);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm11, dst, src);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm12, zmm9, zmm10);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm14, src, zmm9);
                        code = EmitInstruction(code, InstructionOpCode.WideMul, zmm13, dst, zmm10);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm9, zmm11, 0x20);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm14, zmm9, zmm14);
                        code = EmitInstruction(code, InstructionOpCode.And, zmm9, zmm14, zmm24);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm13, zmm9, zmm13);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm13, zmm13, 0x20);
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightLogical, zmm14, zmm14, 0x20);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm13, zmm13, zmm14);
                        code = EmitInstruction(code, InstructionOpCode.Add, zmm13, zmm13, zmm12);

                        //Fixes sign bit
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightArithmetic, zmm10, dst, 0x3f);
                        code = EmitInstruction(code, InstructionOpCode.And, zmm10, zmm10, src);//vpandq  zmm10, zmm10, src
                        code = EmitInstruction(code, InstructionOpCode.Sub, zmm10, zmm13, zmm10);//vpsubq zmm10, zmm13, zmm10
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftRightArithmetic, zmm11, src, 0x3f);//vpsraq  zmm11, src, 0x3f
                        code = EmitInstruction(code, InstructionOpCode.And, zmm11, zmm11, dst); //vpandq  zmm11, zmm11, dst
                        code = EmitInstruction(code, InstructionOpCode.Sub, finalDst, zmm10, zmm11, mask: maskRegister); //vpsubq  dst ,zmm10,zmm11

                        if (targetStart != default)
                        {
                            //mulHiResult = smulhi(src, dst) & 0xFFFFFFFF
                            code = EmitInstruction(code, InstructionOpCode.And, zmm15, finalDst, zmm24);
                        }


                        break;
                    case OpCode.Mul:
                        //dst = dst * src
                        code = EmitMullo(code, finalDst, dst, src, mask: maskRegister);
                        break;
                    case OpCode.AddShift:
                        if (instruction.Operand == 0)
                        {
                            //dst = dst + src
                            code = EmitInstruction(code, InstructionOpCode.Add, finalDst, dst, src, mask: maskRegister);
                        }
                        else
                        {
                            //dst = dst + (src << operand)
                            code = EmitImmediateByteInstruction(code, InstructionOpCode.ShiftLeftLogical, zmm8, src, (byte)instruction.Operand); //Shift left
                            code = EmitInstruction(code, InstructionOpCode.Add, finalDst, dst, zmm8, mask: maskRegister); //Add
                        }
                        break;
                    case OpCode.Sub:
                        //dst = dst - src
                        code = EmitInstruction(code, InstructionOpCode.Sub, finalDst, dst, src, mask: maskRegister);
                        break;
                    case OpCode.Xor:
                        //dst = dst ^ src
                        code = EmitInstruction(code, InstructionOpCode.Xor, finalDst, dst, src, mask: maskRegister);
                        break;
                    case OpCode.Rotate:
                        //dst = ror(dst, operand)
                        code = EmitImmediateByteInstruction(code, InstructionOpCode.RotateRight, finalDst, dst, (byte)instruction.Operand, mask: maskRegister);
                        break;

                    case OpCode.AddConst:
                        {
                            //dst = dst + operand
                            code = EmitRipLoadInstruction(code, InstructionOpCode.Add, finalDst, dst, constDataLoc, mask: maskRegister); //Load from rip
                            constDataLoc += VectorSize;
                        }
                        break;
                    case OpCode.XorConst:
                        {
                            //dst = dst ^ operand
                            code = EmitRipLoadInstruction(code, InstructionOpCode.Xor, finalDst, dst, constDataLoc, mask: maskRegister); //Load from rip
                            constDataLoc += VectorSize;
                        }
                        break;
                }

                //if (isBreak == 1)
                //{
                //    break;
                //}
            }

            #endregion

            //Deal with ending siphash calculations to save on stores/loads
            code = Final(code);

            //zmm0 contains the result, store it into current location
            code = EmitBytes(code, 0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x01); //vmovdqu64 [rcx], zmm0
            code = EmitInstruction(code, InstructionOpCode.Add, zmm31, zmm31, zmm25); //Increase input values by 8

            //LoopCount += 8
            code = EmitBytes(code, 0x49, 0x83, 0xC0, 0x08); //r8 += 8
            code = EmitBytes(code, 0x48, 0x83, 0xC1, 0x40); //rcx += 64 (Set next 8 ulong location for next iteration)

            //LoopCount == 65536
            code = EmitBytes(code, 0x49, 0x81, 0xF8, 0x00, 0x00, 0x01, 0x00); //cmp r8, 65536
            code = EmitBytes(code, 0x0F, 0x85); //jne
            code = EmitU32(code, (uint)(loopStart - code - 4));


            code = EmitBytes(code, avx512Epilogue);

            ulong codeSize = (ulong)(code - codeStart);
            ulong constDataSize = ((ulong)(constDataEnd - constDataStart));

            Debug.Assert(codeSize + constDataSize <= CodeSize);
            return (delegate* unmanaged[Cdecl]<ulong*, void>)codeStart;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* Start(byte* code, byte* constDataLoc)
        {
            code = EmitBytes(code,
                0x62, 0x91, 0xA5, 0x40, 0xEF, 0xCE, //vpxorq zmm1, zmm27, zmm30
                0x62, 0x91, 0x95, 0x40, 0xEF, 0xDF,  //vpxorq zmm3, zmm29, zmm31
                0x62, 0xF1, 0xAD, 0x40, 0xD4, 0xC1, //vpaddq zmm0, zmm26, zmm1
                0x62, 0xF1, 0x9D, 0x40, 0xD4, 0xD3 //vpaddq zmm2, zmm28, zmm3
                );

            //Custom first SipRound
            code = EmitBytes(code,
                0x62, 0xF1, 0xF5, 0x48, 0x72, 0xC9, 0x0D,   //vprolq zmm1, zmm1, 13
                0x62, 0xF1, 0xE5, 0x48, 0x72, 0xCB, 0x10,   //vprolq zmm3, zmm3, 16
                0x62, 0xF1, 0xF5, 0x48, 0xEF, 0xC8,         //vpxorq zmm1, zmm1, zmm0
                0x62, 0xF1, 0xE5, 0x48, 0xEF, 0xDA,         //vpxorq zmm3, zmm3, zmm2
                0x62, 0xF1, 0xFD, 0x48, 0x72, 0xC8, 0x20,   //vprolq zmm0, zmm0, 32
                0x62, 0xF1, 0xED, 0x48, 0xD4, 0xD1,         //vpaddq zmm2, zmm2, zmm1
                0x62, 0xF1, 0xF5, 0x48, 0x72, 0xC9, 0x11,   //vprolq zmm1, zmm1, 17
                0x62, 0xF1, 0xFD, 0x48, 0xD4, 0xC3,         //vpaddq zmm0, zmm0, zmm3
                0x62, 0xF1, 0xE5, 0x48, 0x72, 0xCB, 0x15,   //vprolq zmm3, zmm3, 21
                0x62, 0xF1, 0xF5, 0x48, 0xEF, 0xCA,         //vpxorq zmm1, zmm1, zmm2
                0x62, 0xF1, 0xE5, 0x48, 0xEF, 0xD8,         //vpxorq zmm3, zmm3, zmm0
                0x62, 0xF1, 0xED, 0x48, 0x72, 0xCA, 0x20    //vprolq zmm2, zmm2, 32
                );

            code = SipRoundLow(code);

            code = EmitBytes(code,
                0x62, 0x91, 0xFD, 0x48, 0xEF, 0xC7, //vpxorq zmm0, zmm0, zmm31
                0x62, 0x91, 0xED, 0x48, 0xEF, 0xD6  //vpxorq zmm2, zmm2, zmm30
                );

            code = SipRoundLow(code);
            code = SipRoundLow(code);
            code = SipRoundLow(code);
            code = SipRoundLow(code);

            //vpxorq zmm5, zmm1, [rip + ..]
            code = EmitBytes(code, 0x62, 0xF1, 0xF5, 0x48, 0xEF, 0x2D);
            code = EmitU32(code, (uint)(constDataLoc - code - 4));



            code = EmitBytes(code,
                0x62, 0xF1, 0xFD, 0x48, 0x6F, 0xE0, //vmovdqa64 zmm4, zmm0
                0x62, 0xF1, 0xFD, 0x48, 0x6F, 0xF2, ////vmovdqa64 zmm6, zmm2
                0x62, 0xF1, 0xFD, 0x48, 0x6F, 0xFB ////vmovdqa64 zmm7, zmm3
            );

            code = SipRoundHigh(code);
            code = SipRoundHigh(code);
            code = SipRoundHigh(code);
            code = SipRoundHigh(code);

            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* Final(byte* code)
        {
            code = EmitBytes(code,
                0x62, 0x91, 0xFD, 0x48, 0xD4, 0xC2,  //vpaddq zmm0, zmm0, zmm26
                0x62, 0x91, 0xF5, 0x48, 0xD4, 0xCB //vpaddq zmm1, zmm1, zmm27
               );

            code = SipRoundLow(code);

            code = EmitBytes(code,
                0x62, 0x91, 0xCD, 0x48, 0xD4, 0xF4, //vpaddq zmm6, zmm6, zmm28
                0x62, 0x91, 0xC5, 0x48, 0xD4, 0xFD //vpaddq zmm7, zmm7, zmm29
                );

            code = SipRoundHigh(code);

            code = EmitBytes(code,
                0x62, 0xF1, 0xFD, 0x48, 0xEF, 0xC4 //vpxorq zmm0, zmm0, zmm4
                );

            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* SipRoundLow(byte* code)
        {
            /*
                vpaddq zmm0, zmm0, zmm1
                vpaddq zmm2, zmm2, zmm3
                vprolq zmm1, zmm1, 13
                vprolq zmm3, zmm3, 16
                vpxorq zmm1, zmm1, zmm0
                vpxorq zmm3, zmm3, zmm2
                vprolq zmm0, zmm0, 32


                vpaddq zmm2, zmm2, zmm1
                vpaddq zmm0, zmm0, zmm3
                vprolq zmm1, zmm1, 17
                vprolq zmm3, zmm3, 21
                vpxorq zmm1, zmm1, zmm2
                vpxorq zmm3, zmm3, zmm0
                vprolq zmm2, zmm2, 32
            */

            return EmitBytes(code,
                0x62, 0xF1, 0xFD, 0x48, 0xD4, 0xC1,         //vpaddq zmm0, zmm0, zmm1
                0x62, 0xF1, 0xED, 0x48, 0xD4, 0xD3,         //vpaddq zmm2, zmm2, zmm3

                0x62, 0xF1, 0xF5, 0x48, 0x72, 0xC9, 0x0D,   //vprolq zmm1, zmm1, 13
                0x62, 0xF1, 0xE5, 0x48, 0x72, 0xCB, 0x10,   //vprolq zmm3, zmm3, 16

                0x62, 0xF1, 0xF5, 0x48, 0xEF, 0xC8,         //vpxorq zmm1, zmm1, zmm0
                0x62, 0xF1, 0xE5, 0x48, 0xEF, 0xDA,         //vpxorq zmm3, zmm3, zmm2

                0x62, 0xF1, 0xFD, 0x48, 0x72, 0xC8, 0x20,   //vprolq zmm0, zmm0, 32
                0x62, 0xF1, 0xED, 0x48, 0xD4, 0xD1,         //vpaddq zmm2, zmm2, zmm1

                0x62, 0xF1, 0xF5, 0x48, 0x72, 0xC9, 0x11,   //vprolq zmm1, zmm1, 17
                0x62, 0xF1, 0xFD, 0x48, 0xD4, 0xC3,         //vpaddq zmm0, zmm0, zmm3
                0x62, 0xF1, 0xE5, 0x48, 0x72, 0xCB, 0x15,   //vprolq zmm3, zmm3, 21

                0x62, 0xF1, 0xF5, 0x48, 0xEF, 0xCA,         //vpxorq zmm1, zmm1, zmm2
                0x62, 0xF1, 0xE5, 0x48, 0xEF, 0xD8,         //vpxorq zmm3, zmm3, zmm0
                0x62, 0xF1, 0xED, 0x48, 0x72, 0xCA, 0x20    //vprolq zmm2, zmm2, 32
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* SipRoundHigh(byte* code)
        {
            /*
                vpaddq zmm4, zmm4, zmm5
                vpaddq zmm6, zmm6, zmm7
                vprolq zmm5, zmm5, 13
                vprolq zmm7, zmm7, 16
                vpxorq zmm5, zmm5, zmm4
                vpxorq zmm7, zmm7, zmm6
                vprolq zmm4, zmm4, 32


                vpaddq zmm6, zmm6, zmm5
                vpaddq zmm4, zmm4, zmm7
                vprolq zmm5, zmm5, 17
                vprolq zmm7, zmm7, 21
                vpxorq zmm5, zmm5, zmm6
                vpxorq zmm7, zmm7, zmm4
                vprolq zmm6, zmm6, 32
            */

            return EmitBytes(code,
                0x62, 0xF1, 0xDD, 0x48, 0xD4, 0xE5,
                0x62, 0xF1, 0xCD, 0x48, 0xD4, 0xF7,
                0x62, 0xF1, 0xD5, 0x48, 0x72, 0xCD, 0x0D,
                0x62, 0xF1, 0xC5, 0x48, 0x72, 0xCF, 0x10,
                0x62, 0xF1, 0xD5, 0x48, 0xEF, 0xEC,
                0x62, 0xF1, 0xC5, 0x48, 0xEF, 0xFE,
                0x62, 0xF1, 0xDD, 0x48, 0x72, 0xCC, 0x20,
                0x62, 0xF1, 0xCD, 0x48, 0xD4, 0xF5,
                0x62, 0xF1, 0xDD, 0x48, 0xD4, 0xE7,
                0x62, 0xF1, 0xD5, 0x48, 0x72, 0xCD, 0x11,
                0x62, 0xF1, 0xC5, 0x48, 0x72, 0xCF, 0x15,
                0x62, 0xF1, 0xD5, 0x48, 0xEF, 0xEE,
                0x62, 0xF1, 0xC5, 0x48, 0xEF, 0xFC,
                0x62, 0xF1, 0xCD, 0x48, 0x72, 0xCE, 0x20
                );
        }

        #region AVX512 Encoding

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitImmediateByteInstruction(byte* code, InstructionOpCode op, int dst, int src, byte immediate, int mask = 0)
        {
            code = EmitInstruction(code, op, 0, dst, src, (byte)((int)op >> 8), mask);
            code = EmitByte(code, immediate);

            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitRipLoadInstruction(byte* code, InstructionOpCode op, int dst, int src, byte* loc, int mask = 0)
        {
            code = EmitInstruction(code, op, dst, src, 0, mask: mask, modrm: 0x05); //Load from rip
            code = EmitU32(code, (uint)(loc - code - 4));

            return code;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitShuffle(byte* code, int dst, int src, byte immediate, int mask = 0)
        {
            code = EmitInstruction(code, InstructionOpCode.Shuffle, dst, 0, src, mask: mask, w: 0);
            code = EmitByte(code, immediate);
            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitMullo(byte* code, int dst, int src1, int src2, int mask = 0)
        {
            code = EmitInstruction(code, InstructionOpCode.Mullo, dst, src1, src2, mask: mask, pp: 0x02);

            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitBlend(byte* code, int dst, int src1, int src2, int mask = 0)
        {
            code = EmitInstruction(code, InstructionOpCode.Blend, dst, src1, src2, mask: mask, pp: 0x02);

            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* EmitInstruction(byte* code, InstructionOpCode op, int dst, int src1, int src2, byte immediate = 0, int mask = 0, byte modrm = 0xC0, byte w = 1, byte pp = 0x01)
        {
            byte R = (byte)(~(dst >> 3) & 3);

            int evex = 0x62;

            evex |= (((R & 1) << 7)
                              | ((~(src2 >> 3) & 3) << 5)
                              | ((R >> 1) << 4)
                              | pp
                              ) << 8;


            byte inv_vvvv = (byte)(~((src1 & 0xF)));
            evex |= ((w << 7)
                              | ((inv_vvvv << 3) & 0x7F)
                              | (1 << 2)
                              | 0x1) << 16;


            byte Vprime = (byte)((~(src1 >> 4) & 1));
            evex |= (((0) << 7)
                              | (2 << 5)
                              | (0 << 4)
                              | (Vprime << 3)
                              | mask) << 24;

            byte mm = (byte)(modrm | ((dst & 0x7) << 3) | (src2 & 0x7) | immediate << 4);
            code = EmitU32(code, (uint)evex);
            code = EmitByte(code, (byte)op);
            code = EmitByte(code, mm);

            return code;
        }

        #endregion
    }
}