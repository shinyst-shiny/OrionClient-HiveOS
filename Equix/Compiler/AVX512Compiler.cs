using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace DrillX.Compiler
{
    public unsafe class AVX512Compiler
    {
        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CompiledVectorHash(ulong* r);

        public static readonly uint CodeSize = (uint)AlignSize(HashxProgramMaxSize * CompAvgInstrSize + CompReserveSize, CompPageSize);

        private const int HashxProgramMaxSize = 512;
        private const int CompReserveSize = 1024 * 8;
        private const int CompAvgInstrSize = 8; // Increased due to AVX-512 instruction length
        private const int CompPageSize = 4096;

        private static int AlignSize(int pos, int align)
        {
            return ((((pos) - 1) / (align) + 1) * (align));
        }

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

        public static delegate* unmanaged[Cdecl]<ulong*, void> HashCompileAVX512(Span<Instruction> instructions, byte* code)
        {
            byte* codeStart = code;

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

            byte* target = null;

            Debug.Assert(instructions.Length == 512);
            int isBreak = 0;

            code = EmitBytes(code, 0xC5, 0xF4, 0x46, 0xC9); //Set k1 to 1s
            code = EmitBytes(code, 0xC5, 0xEC, 0x46, 0xD2); //Set k2 to 1s
            code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00); //Sets isBranched to 0

            byte maskRegister = 0;
            int counter = 0;
            bool earlyAddShift = false;

            for (int i = 0; i < instructions.Length; i++)
            {
                var instruction = instructions[i];
                Instruction? nextInstruction = i < 510 ? instructions[i + 1] : null;

                //Branches and Target is always surrounded with a multiply
                if (nextInstruction?.Type == OpCode.AddShift && nextInstruction?.Operand != 0)
                {
                    if (nextInstruction?.Src != instruction.Dst)
                    {
                        earlyAddShift = true;

                        //We can shift the source early
                        code = EmitBytes(code, 0x62, 0xf1, 0x8d, (byte)(0x40 | maskRegister), 0x73, (byte)(0xF0 | nextInstruction.Value.Src), (byte)nextInstruction.Value.Operand); //vpsllq zmm30,src,operand 
                    }
                }

                switch (instruction.Type)
                {
                    case OpCode.Target:
                        target = code;
                        maskRegister = 1;
                        break;
                    case OpCode.Branch:
                        code = EmitBytes(code, 0x4D, 0x85, 0xC0); //test r8, r8 
                        code = EmitBytes(code, 0x75, 0x3D); //We're already branched, so jump to mov r8, 0


                        code = EmitBytes(code, 0x48, 0xb8); //movabs rax, OPERAND
                        code = EmitU64(code, (ulong)instruction.Operand);

                        code = EmitBytes(code, 0x62, 0x72, 0xFD, 0x48, 0x7C, 0xC8); //vpbroadcastq zmm9,rax
                        code = EmitBytes(code, 0x62, 0x51, 0x85, 0x48, 0xDB, 0xC1); //vpandq zmm8, zmm15, zmm9

                        code = EmitBytes(code, 0x62, 0x51, 0xB5, 0x48, 0xEF, 0xC9); //vpxorq zmm9,zmm9,zmm9 


                        code = EmitBytes(code, 0x62, 0xD3, 0xBD, 0x48, 0x1F, 0xC9, 0x00); //vpcmpeqq k1,zmm8,zmm9 

                        code = EmitBytes(code, 0xC5, 0xF4, 0x41, 0xCA); //kandw k1, k1, k2 
                        code = EmitBytes(code, 0xC5, 0xF8, 0x98, 0xC9); //kortestw k1, k1
                        code = EmitBytes(code, 0x74, 0x17); //Skip branch



                        code = EmitBytes(code, 0xC5, 0xF5, 0x42, 0xD2); //kandnb k2, k1, k2
                        code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x01, 0x00, 0x00, 0x00); //mov r8, 1

                        var offset = (uint)(target - code - 5);

                        code = EmitByte(code, 0xE9); //jmp Offset
                        code = EmitU32(code, offset);

                        code = EmitBytes(code, 0x49, 0xC7, 0xC0, 0x00, 0x00, 0x00, 0x00); //mov r8, 0
                        code = EmitBytes(code, 0xC5, 0xF4, 0x46, 0xC9); //Set k1 to 1s

                        maskRegister = 0;

                        break;
                    case OpCode.UMulH:
                        code = EmitBytes(code, 0x62, 0x53, 0x3D, 0x48, 0x25, 0xC0, 0xFF); //vpternlogd zmm8,zmm8,zmm8,0xff -- Sets to all 1s
                        code = EmitBytes(code, 0x62, 0xD1, 0xBD, 0x48, 0x73, 0xD0, 0x20); //vpsrlq zmm8,zmm8,0x20 



                        code = EmitBytes(code, 0x62, 0x71, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xE8 | (instruction.Dst))); //vmovdqa64 zmm13,dst
                        code = EmitBytes(code, 0x62, 0x71, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xF0 | (instruction.Src))); //vmovdqa64 zmm14,src

                        code = EmitBytes(code,
                            0x62, 0x51, 0x7D, 0x48, 0x70, 0xCD, 0xB1, //vpshufd zmm9, zmm13, 0xb1
                            0x62, 0x51, 0x7D, 0x48, 0x70, 0xD6, 0xB1, //vpshufd zmm10, zmm14 0xb1
                            0x62, 0x51, 0x95, 0x48, 0xF4, 0xDE, //vpmuludq zmm11, zmm13, zmm14
                            0x62, 0x51, 0xB5, 0x48, 0xF4, 0xE2, //vpmuludq zmm12, zmm9, zmm10

                            0x62, 0x51, 0x95, 0x48, 0xF4, 0xEA, //vpmuludq zmm13, zmm13, zmm10
                            0x62, 0x51, 0x8D, 0x48, 0xF4, 0xF1, //vpmuludq zmm14 zmm14 zmm9
                            0x62, 0xD1, 0xB5, 0x48, 0x73, 0xD3, 0x20, //vpsrlq zmm9, zmm11, 0x20
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xF6, //vpaddq zmm14 zmm9, zmm14

                            0x62, 0x51, 0x8D, 0x48, 0xDB, 0xC8, //vpandq zmm9, zmm14, zmm8
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xED, //vpaddq zmm13, zmm9, zmm13
                            0x62, 0xD1, 0x95, 0x48, 0x73, 0xD5, 0x20, //vpsrlq zmm13, zmm13, 0x20
                            0x62, 0xD1, 0x8D, 0x48, 0x73, 0xD6, 0x20, //vpsrlq zmm14 zmm14 0x20
                            0x62, 0x51, 0x95, 0x48, 0xD4, 0xEE, //vpaddq zmm13, zmm13, zmm14
                            0x62, 0x51, 0x95, 0x48, 0xD4, 0xEC, //vpaddq zmm13, zmm13, zmm12
                            0x62, 0x51, 0x95, 0x48, 0xDB, 0xF8 //vpandq zmm15, zmm13, zmm8 //mulHiResult
                            );

                        code = EmitBytes(code, 0x62, 0xd1, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xC5 | (instruction.Dst << 3))); //vmovdqa64 dst,zmm13
                        break;
                    case OpCode.SMulH:
                        code = EmitBytes(code, 0x62, 0x53, 0x3D, 0x48, 0x25, 0xC0, 0xFF); //vpternlogd zmm8,zmm8,zmm8,0xff -- Sets to all 1s
                        code = EmitBytes(code, 0x62, 0xD1, 0xBD, 0x48, 0x73, 0xD0, 0x20); //vpsrlq zmm8,zmm8,0x20 

                        code = EmitBytes(code, 0x62, 0x71, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xE8 | (instruction.Dst))); //vmovdqa64 zmm13,dst
                        code = EmitBytes(code, 0x62, 0x71, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xF0 | (instruction.Src))); //vmovdqa64 zmm14,src

                        code = EmitBytes(code,
                            0x62, 0x51, 0x7D, 0x48, 0x70, 0xCD, 0xB1, //vpshufd zmm9, zmm13, 0xb1
                            0x62, 0x51, 0x7D, 0x48, 0x70, 0xD6, 0xB1, //vpshufd zmm10, zmm14 0xb1
                            0x62, 0x51, 0x95, 0x48, 0xF4, 0xDE, //vpmuludq zmm11, zmm13, zmm14
                            0x62, 0x51, 0xB5, 0x48, 0xF4, 0xE2, //vpmuludq zmm12, zmm9, zmm10
                            0x62, 0x51, 0x8D, 0x48, 0xF4, 0xF1, //vpmuludq zmm14 zmm14 zmm9

                            0x62, 0xD1, 0xB5, 0x48, 0x73, 0xD3, 0x20, //vpsrlq zmm9, zmm11, 0x20
                            0x62, 0x51, 0x95, 0x48, 0xF4, 0xEA, //vpmuludq zmm13, zmm13, zmm10
                            0x62, 0xF1, 0xAD, 0x48, 0x72, (byte)(0xE0 | (instruction.Dst)), 0x3F, //vpsraq  zmm10, dst, 0x3f --
                            0x62, 0x51, 0xB5, 0x48, 0xD4, 0xF6, //vpaddq zmm14 zmm9, zmm14
                            0x62, 0x51, 0x8D, 0x48, 0xDB, 0xC8, //vpandq zmm9, zmm14, zmm8
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

                        code = EmitBytes(code, 0x62, 0x51, 0xAD, 0x48, 0xFB, 0xEB); //vpsubq zmm13,zmm10,zmm11 
                        code = EmitBytes(code, 0x62, 0x51, 0x95, 0x48, 0xDB, 0xF8); //vpandq zmm15, zmm13, zmm8 //mulHiResult
                        code = EmitBytes(code, 0x62, 0xd1, 0xfd, (byte)(0x48 | maskRegister), 0x6f, (byte)(0xC5 | (instruction.Dst << 3))); //vmovdqa64 dst,zmm13

                        break;
                    case OpCode.Mul:
                        // Perform 64-bit unsigned multiply: vpmullq zmm{dst}, zmm{dst}, zmm{src}
                        code = EmitBytes(code, 0x62, 0xf2, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0x40, (byte)(0xC0 | (instruction.Dst << 3) | instruction.Src));

                        //isBreak = true;
                        break;
                    case OpCode.AddShift:
                        if (earlyAddShift)
                        {
                            earlyAddShift = false;
                            code = EmitBytes(code, 0x62, 0x91, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC6 | (instruction.Dst << 3)));
                        }
                        else
                        {
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
                            if (nextInstruction?.Type == OpCode.XorConst)
                            {
                                //movabs rax, operand
                                code = EmitBytes(code, 0x48, 0xb8);
                                code = EmitU64(code, (ulong)instruction.Operand);

                                code = EmitBytes(code, 0x48, 0xba);
                                code = EmitU64(code, (ulong)nextInstruction.Value.Operand);

                                code = EmitBytes(code, 0x62, 0x72, 0xfd, (byte)(0x48 | maskRegister), 0x7c, 0xc0); //vpbroadcastq zmm8,rax
                                code = EmitBytes(code, 0x62, 0x72, 0xFD, (byte)(0x48 | maskRegister), 0x7C, 0xCA); //vpbroadcastq zmm9,rdx

                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC0 | (instruction.Dst << 3)));

                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (nextInstruction.Value.Dst << 3)), (byte)(0x48 | maskRegister), 0xef, (byte)(0xC1 | (nextInstruction.Value.Dst << 3))); //vpaddq dst, dst, rdx

                                i++;
                            }
                            else
                            {
                                //movabs rax, operand
                                code = EmitBytes(code, 0x48, 0xb8);
                                code = EmitU64(code, (ulong)instruction.Operand);

                                code = EmitBytes(code, 0x62, 0x72, 0xfd, (byte)(0x48 | maskRegister), 0x7c, 0xc0); //vpbroadcastq zmm8,rax
                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC0 | (instruction.Dst << 3)));
                            }

                        }
                        break;
                    case OpCode.XorConst:
                        {
                            if (nextInstruction?.Type == OpCode.AddConst)
                            {
                                //movabs rax, operand
                                code = EmitBytes(code, 0x48, 0xb8);
                                code = EmitU64(code, (ulong)instruction.Operand);

                                code = EmitBytes(code, 0x48, 0xba);
                                code = EmitU64(code, (ulong)nextInstruction.Value.Operand);


                                code = EmitBytes(code, 0x62, 0x72, 0xfd, (byte)(0x48 | maskRegister), 0x7c, 0xc0); //vpbroadcastq zmm8,rax
                                code = EmitBytes(code, 0x62, 0x72, 0xFD, (byte)(0x48 | maskRegister), 0x7C, 0xCA); //vpbroadcastq zmm9,rdx


                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xef, (byte)(0xC0 | (instruction.Dst << 3)));
                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (nextInstruction.Value.Dst << 3)), (byte)(0x48 | maskRegister), 0xd4, (byte)(0xC1 | (nextInstruction.Value.Dst << 3)));

                                i++;
                            }
                            else
                            {
                                //movabs rax, operand
                                code = EmitBytes(code, 0x48, 0xb8);
                                code = EmitU64(code, (ulong)instruction.Operand);

                                code = EmitBytes(code, 0x62, 0x72, 0xfd, (byte)(0x48 | maskRegister), 0x7c, 0xc0); //vpbroadcastq zmm8,rax
                                code = EmitBytes(code, 0x62, 0xd1, (byte)(0xFD - (instruction.Dst << 3)), (byte)(0x48 | maskRegister), 0xef, (byte)(0xC0 | (instruction.Dst << 3)));
                            }
                        }
                        break;
                }

                if (isBreak == 2)
                {
                    break;
                }
            }

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

            return (delegate* unmanaged[Cdecl]<ulong*, void>)codeStart;
        }
    }
}