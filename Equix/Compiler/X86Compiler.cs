using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Tmds.Linux;
using System.Runtime.CompilerServices;
using System.Security;
using System.ComponentModel;
using Windows.Win32.System.Memory;
using System.Diagnostics;

namespace DrillX.Compiler
{
    public unsafe class X86Compiler
    {
        [SuppressUnmanagedCodeSecurity]
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CompiledGetHash(ulong* r);

        public static readonly uint CodeSize = (uint)AlignSize(HashxProgramMaxSize * CompAvgInstrSize + CompReserveSize, CompPageSize);

		private const int HashxProgramMaxSize = 512;
		private const int CompReserveSize = 1024;
		private const int CompAvgInstrSize = 5;
		private const int CompPageSize = 4096;

		private static int AlignSize(int pos, int align)
		{
			return ((((pos) - 1) / (align) + 1) * (align));
        }

        public readonly static byte[] x86Prologue_Windows = new byte[]
        {

            0x4C, 0x89, 0x64, 0x24, 0x08, /* mov qword ptr [rsp+8], r12 */
			0x4C, 0x89, 0x6C, 0x24, 0x10, /* mov qword ptr [rsp+16], r13 */
			0x4C, 0x89, 0x74, 0x24, 0x18, /* mov qword ptr [rsp+24], r14 */
			0x4C, 0x89, 0x7C, 0x24, 0x20, /* mov qword ptr [rsp+32], r15 */
			0x48, 0x83, 0xEC, 0x10,       /* sub rsp, 16 */
			0x48, 0x89, 0x34, 0x24,       /* mov qword ptr [rsp+0], rsi */
			0x48, 0x89, 0x7C, 0x24, 0x08, /* mov qword ptr [rsp+8], rdi */


			0x31, 0xF6,                   /* xor esi, esi */
			0x8D, 0x7E, 0xFF,             /* lea edi, [rsi-1] */
			0x4C, 0x8B, 0x01,             /* mov r8, qword ptr [rcx+0] */
			0x4C, 0x8B, 0x49, 0x08,       /* mov r9, qword ptr [rcx+8] */
			0x4C, 0x8B, 0x51, 0x10,       /* mov r10, qword ptr [rcx+16] */
			0x4C, 0x8B, 0x59, 0x18,       /* mov r11, qword ptr [rcx+24] */
			0x4C, 0x8B, 0x61, 0x20,       /* mov r12, qword ptr [rcx+32] */
			0x4C, 0x8B, 0x69, 0x28,       /* mov r13, qword ptr [rcx+40] */
			0x4C, 0x8B, 0x71, 0x30,       /* mov r14, qword ptr [rcx+48] */
			0x4C, 0x8B, 0x79, 0x38        /* mov r15, qword ptr [rcx+56] */
        };

        public readonly static byte[] x86Prologue_Linux = new byte[]
        {
			0x48, 0x89, 0xF9,             /* mov rcx, rdi */
			0x48, 0x83, 0xEC, 0x20,       /* sub rsp, 32 */
			0x4C, 0x89, 0x24, 0x24,       /* mov qword ptr [rsp+0], r12 */
			0x4C, 0x89, 0x6C, 0x24, 0x08, /* mov qword ptr [rsp+8], r13 */
			0x4C, 0x89, 0x74, 0x24, 0x10, /* mov qword ptr [rsp+16], r14 */
			0x4C, 0x89, 0x7C, 0x24, 0x18, /* mov qword ptr [rsp+24], r15 */

			0x31, 0xF6,                   /* xor esi, esi */
			0x8D, 0x7E, 0xFF,             /* lea edi, [rsi-1] */
			0x4C, 0x8B, 0x01,             /* mov r8, qword ptr [rcx+0] */
			0x4C, 0x8B, 0x49, 0x08,       /* mov r9, qword ptr [rcx+8] */
			0x4C, 0x8B, 0x51, 0x10,       /* mov r10, qword ptr [rcx+16] */
			0x4C, 0x8B, 0x59, 0x18,       /* mov r11, qword ptr [rcx+24] */
			0x4C, 0x8B, 0x61, 0x20,       /* mov r12, qword ptr [rcx+32] */
			0x4C, 0x8B, 0x69, 0x28,       /* mov r13, qword ptr [rcx+40] */
			0x4C, 0x8B, 0x71, 0x30,       /* mov r14, qword ptr [rcx+48] */
			0x4C, 0x8B, 0x79, 0x38        /* mov r15, qword ptr [rcx+56] */
        };

        public readonly static byte[] x86Epilogue_Windows = new byte[]
        {
            0x4C, 0x89, 0x01,             /* mov qword ptr [rcx+0], r8 */
			0x4C, 0x89, 0x49, 0x08,       /* mov qword ptr [rcx+8], r9 */
			0x4C, 0x89, 0x51, 0x10,       /* mov qword ptr [rcx+16], r10 */
			0x4C, 0x89, 0x59, 0x18,       /* mov qword ptr [rcx+24], r11 */
			0x4C, 0x89, 0x61, 0x20,       /* mov qword ptr [rcx+32], r12 */
			0x4C, 0x89, 0x69, 0x28,       /* mov qword ptr [rcx+40], r13 */
			0x4C, 0x89, 0x71, 0x30,       /* mov qword ptr [rcx+48], r14 */
			0x4C, 0x89, 0x79, 0x38,       /* mov qword ptr [rcx+56], r15 */

			0x48, 0x8B, 0x34, 0x24,       /* mov rsi, qword ptr [rsp+0] */
			0x48, 0x8B, 0x7C, 0x24, 0x08, /* mov rdi, qword ptr [rsp+8] */
			0x48, 0x83, 0xC4, 0x10,       /* add rsp, 16 */
			0x4C, 0x8B, 0x64, 0x24, 0x08, /* mov r12, qword ptr [rsp+8] */
			0x4C, 0x8B, 0x6C, 0x24, 0x10, /* mov r13, qword ptr [rsp+16] */
			0x4C, 0x8B, 0x74, 0x24, 0x18, /* mov r14, qword ptr [rsp+24] */
			0x4C, 0x8B, 0x7C, 0x24, 0x20, /* mov r15, qword ptr [rsp+32] */

			0xC3                          /* ret */
        };

        public readonly static byte[] x86Epilogue_Linux = new byte[]
{
            0x4C, 0x89, 0x01,             /* mov qword ptr [rcx+0], r8 */
			0x4C, 0x89, 0x49, 0x08,       /* mov qword ptr [rcx+8], r9 */
			0x4C, 0x89, 0x51, 0x10,       /* mov qword ptr [rcx+16], r10 */
			0x4C, 0x89, 0x59, 0x18,       /* mov qword ptr [rcx+24], r11 */
			0x4C, 0x89, 0x61, 0x20,       /* mov qword ptr [rcx+32], r12 */
			0x4C, 0x89, 0x69, 0x28,       /* mov qword ptr [rcx+40], r13 */
			0x4C, 0x89, 0x71, 0x30,       /* mov qword ptr [rcx+48], r14 */
			0x4C, 0x89, 0x79, 0x38,       /* mov qword ptr [rcx+56], r15 */

			0x4C, 0x8B, 0x24, 0x24,       /* mov r12, qword ptr [rsp+0] */
			0x4C, 0x8B, 0x6C, 0x24, 0x08, /* mov r13, qword ptr [rsp+8] */
			0x4C, 0x8B, 0x74, 0x24, 0x10, /* mov r14, qword ptr [rsp+16] */
			0x4C, 0x8B, 0x7C, 0x24, 0x18, /* mov r15, qword ptr [rsp+24] */
			0x48, 0x83, 0xC4, 0x20,       /* add rsp, 32 */

			0xC3                          /* ret */
};

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static byte* EmitByte(byte* p, byte x) { *p = x; return ++p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU16(byte* p, ushort x) { *(ushort*)p = x; p += 2; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU32(byte* p, uint x) { *(uint*)p = x; p += 4; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte* EmitU64(byte* p, ulong x) { *(ulong*)p = x; p += 8; return p; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GenSIB(uint scale, uint index, uint b) { return ((scale << 6) | (index << 3) | b); }

        public static delegate* unmanaged[Cdecl]<ulong*, void> HashCompileX86(Span<Instruction> instructions, byte* code)
        {
            byte* codeStart = code;


#if !IGNORE_SECURITY_RISK
            // Leaving memory writable+executable is considered a security risk
            // Unfortunately, this appears to lead to performance loss on high core count CPUs
			// You can switch this behavior by uncommenting the lines in the Equix.csproj file
            VirtualMemory.HashxVmRw(code, new nuint(CodeSize));
#endif
			Span<byte> pos = new Span<byte>(code, (int)CodeSize);
			byte* target = null;

			if(OperatingSystem.IsWindows())
            {
                x86Prologue_Windows.AsSpan().CopyTo(pos);
                code += x86Prologue_Windows.Length;
            }
			else if(OperatingSystem.IsLinux())
            {
                x86Prologue_Linux.AsSpan().CopyTo(pos);
                code += x86Prologue_Linux.Length;
            }

			Debug.Assert(instructions.Length == 512);

			for (int i = 0; i < instructions.Length; i++)
			{
				var instruction = instructions[i];

				switch (instruction.Type)
				{
					case OpCode.UMulH:
						code = EmitU64(code, 0x8b4ce0f749c08b49 | (((ulong)instruction.Src) << 40) | (((ulong)instruction.Dst) << 16));
						code = EmitByte(code, (byte)(0xc2 + 8 * instruction.Dst));
						break;
					case OpCode.SMulH:
						code = EmitU64(code, 0x8b4ce8f749c08b49 | (((ulong)instruction.Src) << 40) | (((ulong)instruction.Dst) << 16));
						code = EmitByte(code, (byte)(0xc2 + 8 * instruction.Dst));
						break;
					case OpCode.Mul:
						code = EmitU32(code, (uint)(0xc0af0f4d | ((ulong)instruction.Dst << 27) | ((ulong)instruction.Src << 24)));
						break;
					case OpCode.Sub:
						code = EmitU16(code, 0x2b4d);
						code = EmitByte(code, (byte)(0xc0 | ((ulong)instruction.Dst << 3) | (ulong)(uint)instruction.Src));
						break;
					case OpCode.Xor:
						code = EmitU16(code, 0x334d);
						code = EmitByte(code, (byte)(0xc0 | ((ulong)instruction.Dst << 3) | (ulong)(uint)instruction.Src));
						break;
					case OpCode.AddShift:
						code = EmitU32(code, (uint)(0x00048d4f | (((uint)instruction.Dst << 19) |
						   (GenSIB((uint)instruction.Operand, (uint)instruction.Src, (uint)instruction.Dst) << 24))));
						break;
					case OpCode.Rotate:
						code = EmitU32(code, (uint)(0x00c8c149 | ((uint)instruction.Dst << 16) | ((uint)instruction.Operand << 24)));
						break;
					case OpCode.AddConst:
						code = EmitU16(code, 0x8149);
						code = EmitByte(code, (byte)(0xc0 | instruction.Dst));
						code = EmitU32(code, (uint)instruction.Operand);

						break;
					case OpCode.XorConst:
						code = EmitU16(code, 0x8149);
						code = EmitByte(code, (byte)(0xf0 | instruction.Dst));
						code = EmitU32(code, (uint)instruction.Operand);
						break;
					case OpCode.Target:
						target = code;
						code = EmitU32(code, 0x440fff85);
						code = EmitByte(code, 0xf7);
						break;
					case OpCode.Branch:
						code = EmitU64(code, ((ulong)instruction.Operand) << 32 | 0xc2f7f209);
						code = EmitU16(code, (ushort)(((target - code) << 8) | 0x74));
						break;
				}
			}


            if (OperatingSystem.IsWindows())
            {
                var pos2 = new Span<byte>(code, x86Epilogue_Windows.Length);
                x86Epilogue_Windows.AsSpan().CopyTo(pos2);
            }
            else if (OperatingSystem.IsLinux())
            {
                var pos2 = new Span<byte>(code, x86Epilogue_Linux.Length);
                x86Epilogue_Linux.AsSpan().CopyTo(pos2);
            }

#if !IGNORE_SECURITY_RISK
            VirtualMemory.HashxVmRx(codeStart, CodeSize);
#endif
			return (delegate* unmanaged[Cdecl]<ulong*, void>)codeStart;
        }
    }
}
