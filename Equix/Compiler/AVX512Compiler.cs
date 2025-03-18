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
        public readonly static byte[] avx512Prologue_Windows = new byte[]
        { 
            // First save ZMM30 to stack (allocate 64 bytes for it)
            0x48, 0x83, 0xEC, 0x40,                 // sub rsp, 64
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x34, 0x24,  // vmovdqu64 [rsp], zmm30

            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x01,                 // vmovdqu64 zmm0, [rcx]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x89, 0x40, 0x00, 0x00, 0x00,    // vmovdqu64 zmm1, [rcx+64]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x91, 0x80, 0x00, 0x00, 0x00,    // vmovdqu64 zmm2, [rcx+128]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x99, 0xC0, 0x00, 0x00, 0x00,    // vmovdqu64 zmm3, [rcx+192]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xA1, 0x00, 0x01, 0x00, 0x00,    // vmovdqu64 zmm4, [rcx+256]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xA9, 0x40, 0x01, 0x00, 0x00,    // vmovdqu64 zmm5, [rcx+320]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xB1, 0x80, 0x01, 0x00, 0x00,    // vmovdqu64 zmm6, [rcx+384]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xB9, 0xC0, 0x01, 0x00, 0x00     // vmovdqu64 zmm7, [rcx+448]
        };

        // AVX-512 prologue for Linux - preserves registers and loads vectors
        public readonly static byte[] avx512Prologue_Linux = new byte[]
        {
            // In Linux, first argument is in rdi, move it to rcx for consistency
            0x48, 0x89, 0xF9,                   // mov rcx, rdi

            // First save ZMM30 to stack (allocate 64 bytes for it)
            0x48, 0x83, 0xEC, 0x40,                 // sub rsp, 64
            0x62, 0x61, 0xFE, 0x48, 0x7F, 0x34, 0x24,  // vmovdqu64 [rsp], zmm30
            
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x01,                 // vmovdqu64 zmm0, [rcx]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x89, 0x40, 0x00, 0x00, 0x00,    // vmovdqu64 zmm1, [rcx+64]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x91, 0x80, 0x00, 0x00, 0x00,    // vmovdqu64 zmm2, [rcx+128]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0x99, 0xC0, 0x00, 0x00, 0x00,    // vmovdqu64 zmm3, [rcx+192]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xA1, 0x00, 0x01, 0x00, 0x00,    // vmovdqu64 zmm4, [rcx+256]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xA9, 0x40, 0x01, 0x00, 0x00,    // vmovdqu64 zmm5, [rcx+320]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xB1, 0x80, 0x01, 0x00, 0x00,    // vmovdqu64 zmm6, [rcx+384]
            0x62, 0xF1, 0xFE, 0x48, 0x6F, 0xB9, 0xC0, 0x01, 0x00, 0x00     // vmovdqu64 zmm7, [rcx+448]
        };

        // AVX-512 epilogue for Windows - stores vectors and restores registers
        public readonly static byte[] avx512Epilogue_Windows = new byte[]
        {
            // Store data back into memory from zmm0-zmm7
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x01,                 // vmovdqu64 [rcx], zmm0
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x89, 0x40, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+64], zmm1
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x91, 0x80, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+128], zmm2
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x99, 0xC0, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+192], zmm3
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA1, 0x00, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+256], zmm4
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA9, 0x40, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+320], zmm5
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB1, 0x80, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+384], zmm6
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB9, 0xC0, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+448], zmm7

            // Restore ZMM30 from stack
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x34, 0x24,  // vmovdqu64 zmm30, [rsp]
            0x48, 0x83, 0xC4, 0x40,                 // add rsp, 64

            0xC3                             // ret
        };

        // AVX-512 epilogue for Linux - stores vectors and restores registers
        public readonly static byte[] avx512Epilogue_Linux = new byte[]
        {
            // Store vectors
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x01,         // vmovdqu64 [rcx], zmm8
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x89, 0x40, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+64], zmm1
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x91, 0x80, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+128], zmm2
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0x99, 0xC0, 0x00, 0x00, 0x00,    // vmovdqu64 [rcx+192], zmm3
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA1, 0x00, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+256], zmm4
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xA9, 0x40, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+320], zmm5
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB1, 0x80, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+384], zmm6
            0x62, 0xF1, 0xFE, 0x48, 0x7F, 0xB9, 0xC0, 0x01, 0x00, 0x00,    // vmovdqu64 [rcx+448], zmm7
            
            // Restore ZMM30 from stack
            0x62, 0x61, 0xFE, 0x48, 0x6F, 0x34, 0x24,  // vmovdqu64 zmm30, [rsp]
            0x48, 0x83, 0xC4, 0x40,                 // add rsp, 64

            0xC3                                 // ret
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

        public static delegate* unmanaged[Cdecl]<ulong*, void> HashCompileAVX512(Span<Instruction> instructions, byte* code)
        {
            //Add constant data
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
            }

            byte* codeStart = code;
            byte* constDataLoc = constDataStart;

            // Emit OS-specific prologue
            if (OperatingSystem.IsWindows())
            {
                var pos = new Span<byte>(code, avx512Prologue_Windows.Length);
                avx512Prologue_Windows.AsSpan().CopyTo(pos);
                code += avx512Prologue_Windows.Length;
            }
            else if (OperatingSystem.IsLinux())
            {
                var pos = new Span<byte>(code, avx512Prologue_Linux.Length);
                avx512Prologue_Linux.AsSpan().CopyTo(pos);
                code += avx512Prologue_Linux.Length;
            }

            Debug.Assert(instructions.Length == 512);
            int isBreak = 0;

            code = EmitBytes(code, 0xC5, 0xF4, 0x46, 0xC9); //Set k1 to 1s
            code = EmitBytes(code, 0xC5, 0xEC, 0x46, 0xD2); //Set k2 to 1s
            code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00); //Sets isBranched to 0

            //zmm24 = 0xFFFFFFFF
            code = EmitBytes(code, 0x62, 0x03, 0x3D, 0x40, 0x25, 0xC0, 0xFF); //vpternlogd zmm24,zmm24,zmm24,0xff -- Sets to all 1s
            code = EmitBytes(code, 0x62, 0x91, 0xBD, 0x40, 0x73, 0xD0, 0x20); //vpsrlq zmm24,zmm24,0x20 
            code = EmitBytes(code, 0x62, 0x01, 0xB5, 0x40, 0xEF, 0xC9); //vpxorq zmm25,zmm25,zmm25 


            byte maskRegister = 0;
            byte* targetStart = default;
            byte* targetConstDataLoc = default;
            int counter = 0;
            byte* tester = null;

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
                            code = EmitU32(code, (uint)(branchSize + 100)); //Skip current instruction (4 bytes), branch size, and 48*2 for the store/restore

                            tester = code;

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
                            var asdfadsf = (int)(code - tester);
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

            //code = EmitBytes(code, 0x62, 0xB1, 0xFD, 0x48, 0x6F, 0xC0);

            // Emit OS-specific epilogue
            if (OperatingSystem.IsWindows())
            {
                var pos2 = new Span<byte>(code, avx512Epilogue_Windows.Length);
                avx512Epilogue_Windows.AsSpan().CopyTo(pos2);
                code += avx512Epilogue_Windows.Length;
            }
            else if (OperatingSystem.IsLinux())
            {
                var pos2 = new Span<byte>(code, avx512Epilogue_Linux.Length);
                avx512Epilogue_Linux.AsSpan().CopyTo(pos2);
                code += avx512Epilogue_Linux.Length;
            }

            ulong codeSize = (ulong)(code - codeStart);
            ulong constDataSize = ((ulong)(constDataEnd - constDataStart));

            Debug.Assert(codeSize + constDataSize <= CodeSize);
            return (delegate* unmanaged[Cdecl]<ulong*, void>)codeStart;
        }
    }
}