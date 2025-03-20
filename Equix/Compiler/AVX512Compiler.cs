using Equix.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace DrillX.Compiler
{
    public unsafe class AVX512Compiler : BaseCompiler
    {
        private const int VectorSize = sizeof(ulong) * 8;

        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CompiledVectorHash(ulong* r);

        // AVX-512 prologue for Windows - preserves registers and loads vectors
        public readonly static byte[] avx512Prologue = new byte[]
        { 
            // Saves non-volatile registers zmm24 and zmm25
            0x48, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00,       // sub rsp, 128
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x04, 0x24,       // vmovdqu64 [rsp], zmm24
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x4C, 0x24, 0x01, // vmovdqu64 [rsp+64], zmm25

            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x01,          // vmovdqu64 zmm0, [rcx]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x49, 0x01,    // vmovdqu64 zmm1, [rcx+64]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x51, 0x02,    // vmovdqu64 zmm2, [rcx+128]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x59, 0x03,    // vmovdqu64 zmm3, [rcx+192]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x61, 0x04,    // vmovdqu64 zmm4, [rcx+256]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x69, 0x05,    // vmovdqu64 zmm5, [rcx+320]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x71, 0x06,    // vmovdqu64 zmm6, [rcx+384]
            //0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x79, 0x07     // vmovdqu64 zmm7, [rcx+448]
        };

        // AVX-512 epilogue for Windows - stores vectors and restores registers
        public readonly static byte[] avx512Epilogue = new byte[]
        {
            // Store data back into memory from zmm0-zmm7
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x01,                 // vmovdqu64 [rcx], zmm0
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x89, 0x40, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+64], zmm1
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x91, 0x80, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+128], zmm2
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x99, 0xC0, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+192], zmm3
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA1, 0x00, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+256], zmm4
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA9, 0x40, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+320], zmm5
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB1, 0x80, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+384], zmm6
            //0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB9, 0xC0, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+448], zmm7

            // Restore non-volatile registers
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x04, 0x24,       // vmovdqu64 zmm24, [rsp]
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x4C, 0x24, 0x01, // vmovdqu64 zmm25, [rsp+64]
            0x48, 0x81, 0xC4, 0x80, 0x00, 0x00, 0x00,       // add rsp, 128

            0xC3                             // ret
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitByte(byte* p, byte x) { *p = x; return ++p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitBytes(byte* p, params byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                *p++ = b;
            }
            return p;
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
            //int* lastSave = stackalloc int[8];

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

            #endregion

            byte* codeStart = code;
            byte* constDataLoc = constDataStart;

            #region Prologue

            if (OperatingSystem.IsLinux())
            {
                // In Linux, first argument is in rdi, move it to rcx for consistency
                code = EmitBytes(code, 0x48, 0x83, 0xEC, 0x40);
            }

            code = EmitBytes(code, avx512Prologue);

            code = EmitBytes(code, 0xC5, 0xF4, 0x46, 0xC9); //Set k1 to 1s
            code = EmitBytes(code, 0xC5, 0xEC, 0x46, 0xD2); //Set k2 to 1s
            code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00); //Sets isBranched to 0

            //Set 0xFFFFFFFF and 0x00000000 into registers for later
            code = EmitBytes(code, 0x62, 0x03, 0x3D, 0x40, 0x25, 0xC0, 0xFF); //vpternlogd zmm24,zmm24,zmm24,0xff -- Sets to all 1s
            code = EmitBytes(code, 0x62, 0x91, 0xBD, 0x40, 0x73, 0xD0, 0x20); //vpsrlq zmm24,zmm24,0x20 
            code = EmitBytes(code, 0x62, 0x01, 0xB5, 0x40, 0xEF, 0xC9); //vpxorq zmm25,zmm25,zmm25 

            //Set ee into zmm30
            code = EmitBytes(code, 0x62, 0x61, 0xFE, 0x48, 0x6F, 0x35); //vmovdqu64 zmm30, [rip + ...]
            code = EmitU32(code, (uint)(constDataLoc - code - 4));
            constDataLoc += VectorSize;

            #endregion

            //Load some constants

            code = Start(code, constDataLoc);

            constDataLoc += VectorSize;

            Debug.Assert(instructions.Length == 512);
            int isBreak = 0;
            byte maskRegister = 0;
            byte* targetStart = default;
            byte* targetConstDataLoc = default;
            int counter = 0;
            byte* branchStartLoc = null;
            int* lastSave = stackalloc int[8];

            for (int i = 0; i < instructions.Length; i++)
            {
                var instruction = instructions[i];

                switch (instruction.Type)
                {
                    case OpCode.Target:
                        maskRegister = 0;
                        targetStart = code;
                        targetConstDataLoc = constDataLoc;
                        break;
                    case OpCode.Branch:
                        if (targetStart != default)
                        {
                            var branchSize = (int)(code - targetStart) - 6; //Minus 6 for the unneeded mulHiResult calculation
                            //First time encountering branch
                            targetStart = default;

                            //Get branch mask
                            code = EmitBytes(code, 0x62, 0x71, 0x85, 0x48, 0xDB, 0x05); //vpandq zmm8, zmm15, operand
                            code = EmitU32(code, (uint)(constDataLoc - code - 4));

                            code = EmitBytes(code, 0x62, 0xD2, 0xBE, 0x48, 0x27, 0xD0); //vptestnmq k2,zmm8,zmm8 //Detect which values need to be branched (set to 1)
                            code = EmitBytes(code, 0xC5, 0xED, 0x41, 0xD1); //kandb  k2,k2,k1 //Remove any that have already branched
                            code = EmitBytes(code, 0xC5, 0xF9, 0x98, 0xD2); //kortestb k2, k2 //Check if we can fully skip this branch
                            code = EmitBytes(code, 0x0F, 0x84); //je
                            //335
                            code = EmitU32(code, 0); //Skip current instruction (4 bytes), branch size, and 48*2 for the store/restore

                            branchStartLoc = code;

                            //Store to temp variables
                            code = EmitBytes(code,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xC0,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xC9,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xD2,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xDB,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xE4,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xED,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xF6,
                                0x62, 0xE1, 0xFD, 0x48, 0x6F, 0xFF);

                            //Go back to target + 1
                            i -= 16;
                            constDataLoc = targetConstDataLoc;
                        }
                        else
                        {
                            constDataLoc += VectorSize;
                            //Restore variables
                            code = EmitBytes(code,
                                0x62, 0xF2, 0xFD, 0x42, 0x64, 0xC0,
                                0x62, 0xF2, 0xF5, 0x42, 0x64, 0xC9,
                                0x62, 0xF2, 0xED, 0x42, 0x64, 0xD2,
                                0x62, 0xF2, 0xE5, 0x42, 0x64, 0xDB,
                                0x62, 0xF2, 0xDD, 0x42, 0x64, 0xE4,
                                0x62, 0xF2, 0xD5, 0x42, 0x64, 0xED,
                                0x62, 0xF2, 0xCD, 0x42, 0x64, 0xF6,
                                0x62, 0xF2, 0xC5, 0x42, 0x64, 0xFF);

                            code = EmitBytes(code, 0xC5, 0xED, 0x42, 0xC9); //kandnb k1, k2, k1

                            EmitU32((branchStartLoc - 4), (uint)(code - branchStartLoc)); //Properly set jmp target
                        }

                        maskRegister = 0;
                        break;
                    case OpCode.UMulH:
                        code = EmitBytes(code,
                            0x62, 0x71, 0x7D, 0x48, 0x70, (byte)(0xC8 | instruction.Dst), 0xB1, //vpshufd zmm9, dst, 0xb1
                            0x62, 0x71, 0x7D, 0x48, 0x70, (byte)(0xD0 | instruction.Src), 0xB1, //vpshufd zmm10, src 0xb1
                            0x62, 0x71, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xF4, (byte)(0xD8 | instruction.Src), //vpmuludq zmm11, dst, src
                            0x62, 0x51, 0xB5, 0x48, 0xF4, 0xE2, //vpmuludq zmm12, zmm9, zmm10

                            0x62, 0x51, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xF4, 0xEA, //vpmuludq zmm13, dst, zmm10
                            0x62, 0x51, (byte)(0xFD - (instruction.Src << 3)), 0x48, 0xF4, 0xF1, //vpmuludq zmm14, src, zmm9
                            0x62, 0xD1, 0xB5, 0x48, 0x73, 0xD3, 0x20, //vpsrlq zmm9, zmm11, 0x20
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xF6, //vpaddq zmm14 zmm9, zmm14

                            0x62, 0x11, 0x8D, 0x48, 0xDB, 0xC8, //vpandq zmm9, zmm14, zmm24
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xED, //vpaddq zmm13, zmm9, zmm13
                            0x62, 0xD1, 0x95, 0x48, 0x73, 0xD5, 0x20, //vpsrlq zmm13, zmm13, 0x20
                            0x62, 0xD1, 0x8D, 0x48, 0x73, 0xD6, 0x20, //vpsrlq zmm14 zmm14 0x20
                            0x62, 0x51, 0x95, 0x48, 0xD4, 0xEE, //vpaddq zmm13, zmm13, zmm14
                            0x62, 0xD1, 0x95, 0x48, 0xD4, (byte)(0xC4 | instruction.Dst << 3) //vpaddq dst, zmm13, zmm12
                            );

                        if (targetStart != default)
                        {
                            code = EmitBytes(code, 0x62, 0x11, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xDB, 0xF8); //vpandq zmm15, dst, zmm24 //mulHiResult
                        }
                        break;
                    case OpCode.SMulH:
                        code = EmitBytes(code,
                            0x62, 0x71, 0x7D, 0x48, 0x70, (byte)(0xC8 | instruction.Dst), 0xB1, //vpshufd zmm9, dst, 0xb1
                            0x62, 0x71, 0x7D, 0x48, 0x70, (byte)(0xD0 | instruction.Src), 0xB1, //vpshufd zmm10, src 0xb1
                            0x62, 0x71, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xF4, (byte)(0xD8 | instruction.Src), //vpmuludq zmm11, dst, src
                            0x62, 0x51, 0xB5, 0x48, 0xF4, 0xE2, //vpmuludq zmm12, zmm9, zmm10
                            0x62, 0x51, (byte)(0xFD - (instruction.Src << 3)), 0x48, 0xF4, 0xF1, //vpmuludq zmm14, src, zmm9

                            0x62, 0xD1, 0xB5, 0x48, 0x73, 0xD3, 0x20, //vpsrlq zmm9, zmm11, 0x20
                            0x62, 0x51, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xF4, 0xEA, //vpmuludq zmm13, dst, zmm10
                            0x62, 0xF1, 0xAD, 0x48, 0x72, (byte)(0xE0 | (instruction.Dst)), 0x3F, //vpsraq  zmm10, dst, 0x3f --
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xF6, //vpaddq zmm14 zmm9, zmm14
                            0x62, 0x11, 0x8D, 0x48, 0xDB, 0xC8, //vpandq zmm9, zmm14, zmm24
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xED, //vpaddq zmm13, zmm9, zmm13
                            0x62, 0xD1, 0x95, 0x48, 0x73, 0xD5, 0x20, //vpsrlq zmm13, zmm13, 0x20
                            0x62, 0xD1, 0x8D, 0x48, 0x73, 0xD6, 0x20, //vpsrlq zmm14 zmm14 0x20
                            0x62, 0x51, 0x95, 0x48, 0xD4, 0xEE, //vpaddq zmm13, zmm13, zmm14
                            0x62, 0x51, 0x95, 0x48, 0xD4, 0xEC //vpaddq zmm13, zmm13, zmm12
                            );


                        code = EmitBytes(code, 0x62, 0x71, 0xAD, 0x48, 0xDB, (byte)(0xD0 | (instruction.Src))); //vpandq  zmm10, zmm10, src
                        code = EmitBytes(code, 0x62, 0x51, 0x95, 0x48, 0xFB, 0xD2); //vpsubq  zmm10, zmm13, zmm10
                        code = EmitBytes(code, 0x62, 0xF1, 0xA5, 0x48, 0x72, (byte)(0xE0 | (instruction.Src)), 0x3F); //vpsraq  zmm10, src, 0x3f
                        code = EmitBytes(code, 0x62, 0x71, 0xA5, 0x48, 0xDB, (byte)(0xD8 | (instruction.Dst))); //vpandq  zmm10, zmm10, dst

                        code = EmitBytes(code, 0x62, 0xD1, 0xAD, 0x48, 0xFB, (byte)(0xC3 | instruction.Dst << 3)); //vpsubq zmm13,zmm10,zmm11

                        if (targetStart != default)
                        {
                            code = EmitBytes(code, 0x62, 0x11, (byte)(0xFD - (instruction.Dst << 3)), 0x48, 0xDB, 0xF8); //vpandq zmm15, dst, zmm24 //mulHiResult
                        }

                        break;
                    case OpCode.Mul:
                        // Perform 64-bit unsigned multiply: vpmullq zmm{dst}, zmm{dst}, zmm{src}
                        code = EmitBytes(code, 0x62, 0xf2, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0x40, (byte)(0xC0 | (instruction.Dst << 3) | instruction.Src));

                        //isBreak = true;
                        break;
                    case OpCode.AddShift:
                        if (instruction.Operand == 0)
                        {
                            code = EmitBytes(code, 0x62, 0xF1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC0 | (instruction.Dst << 3 | instruction.Src)));
                        }
                        else
                        {
                            //zmm8 = src << operand
                            code = EmitBytes(code, 0x62, 0xf1, 0xbd, (byte)(0x48 | maskRegister), 0x73, (byte)(0xF0 | instruction.Src), (byte)instruction.Operand);

                            //dst = dst + zmm8
                            code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC0 | (instruction.Dst << 3)));
                        }
                        break;
                    case OpCode.Sub:
                        // vpsubq zmm{dst}{k2}, zmm{dst}, zmm{src}
                        code = EmitBytes(code, 0x62, 0xf1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xfb, (byte)(0xC0 | (instruction.Dst << 3) | instruction.Src));
                        break;
                    case OpCode.Xor:
                        // vpxorq zmm{dst}{k2}, zmm{dst}, zmm{src}
                        code = EmitBytes(code, 0x62, 0xf1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xef, (byte)(0xC0 | (instruction.Dst << 3) | instruction.Src));
                        break;
                    case OpCode.Rotate:
                        code = EmitBytes(code, 0x62, 0xF1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0x72, (byte)(0xC0 | instruction.Dst), (byte)instruction.Operand);
                        break;
                    case OpCode.AddConst:
                        {
                            code = EmitBytes(code, 0x62, 0xf1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0x05 | (instruction.Dst << 3)));
                            code = EmitU32(code, (uint)(constDataLoc - code - 4));

                            constDataLoc += VectorSize;
                        }
                        break;
                    case OpCode.XorConst:
                        {
                            code = EmitBytes(code, 0x62, 0xf1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xef, (byte)(0x05 | (instruction.Dst << 3)));
                            code = EmitU32(code, (uint)(constDataLoc - code - 4));

                            constDataLoc += VectorSize;
                        }
                        break;
                }

                if (isBreak == 1)
                {
                    break;
                }
            }

            //Deal with ending siphash calculations to save on stores/loads
            code = Final(code);
            code = EmitBytes(code, avx512Epilogue);

            ulong codeSize = (ulong)(code - codeStart);
            ulong constDataSize = ((ulong)(constDataEnd - constDataStart));

            Debug.Assert(codeSize + constDataSize <= CodeSize);
            return (delegate* unmanaged[Cdecl]<ulong*, void>)codeStart;
        }

        private static byte* Start(byte* code, byte* constDataLoc)
        {
            //Change these to be lower later
            code = EmitBytes(code,
                0x62, 0x61, 0xFE, 0x48, 0x6F, 0x39,  //vmovdqu64 zmm31, [rcx]  
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x51, 0x08, //vpbroadcastq zmm26, [rcx + 64]
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x59, 0x09, //vpbroadcastq zmm27, [rcx + 72]
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x61, 0x0A, //vpbroadcastq zmm28, [rcx + 80]
                0x62, 0x62, 0xFD, 0x48, 0x59, 0x69, 0x0B //vpbroadcastq zmm29, [rcx + 88]
                );

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
                0x62, 0xF1, 0xFD, 0x48, 0xEF, 0xC4, //vpxorq zmm0, zmm0, zmm4
                0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x01  //vmovdqu64 [rcx], zmm0
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
    }
}