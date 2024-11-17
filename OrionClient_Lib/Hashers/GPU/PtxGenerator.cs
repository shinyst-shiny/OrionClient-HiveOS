using ILGPU.Backends.PTX;
using ILGPU.Backends;
using ILGPU;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.GPU
{
    public static class PtxGenerator
    {
        public static void Orb64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, RegisterAllocator<PTXRegisterKind>.HardwareRegister b)
        {
            var command = codeGenerator.BeginCommand($"or.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        public static void Xorb64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, RegisterAllocator<PTXRegisterKind>.HardwareRegister b)
        {
            var command = codeGenerator.BeginCommand($"xor.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        public static void Xorb64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, ulong b)
        {
            var command = codeGenerator.BeginCommand($"xor.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendConstant(b);
            command.Dispose();
        }

        public static void Addu64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, RegisterAllocator<PTXRegisterKind>.HardwareRegister b)
        {
            var command = codeGenerator.BeginCommand($"add.u64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        public static void ShiftLeftB64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, RegisterAllocator<PTXRegisterKind>.HardwareRegister b)
        {
            var command = codeGenerator.BeginCommand($"shl.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        public static void ShiftLeftB64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, int b)
        {
            var command = codeGenerator.BeginCommand($"shl.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendConstant(b);
            command.Dispose();
        }

        public static void ShiftRightu64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, RegisterAllocator<PTXRegisterKind>.HardwareRegister b)
        {
            var command = codeGenerator.BeginCommand($"shr.u64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendArgument(b);
            command.Dispose();
        }

        public static void ShiftRightu64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a, int b)
        {
            var command = codeGenerator.BeginCommand($"shr.u64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.AppendConstant(b);
            command.Dispose();
        }

        public static void Movb64(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister ret, RegisterAllocator<PTXRegisterKind>.HardwareRegister a)
        {
            var command = codeGenerator.BeginCommand($"mov.b64");
            command.AppendArgument(ret);
            command.AppendArgument(a);
            command.Dispose();
        }


        public static void Rol(this PTXCodeGenerator codeGenerator, RegisterAllocator<PTXRegisterKind>.HardwareRegister v, int amount, RegisterAllocator<PTXRegisterKind>.HardwareRegister temp = null)
        {
            //(ul << N) | (ul >> (64 - N)
            var tempOperand = temp ?? codeGenerator.AllocateRegister(BasicValueType.Int64, PTXRegisterKind.Int64);

            codeGenerator.ShiftLeftB64(tempOperand, v, amount);
            codeGenerator.ShiftRightu64(v, v, 64 - amount);
            codeGenerator.Orb64(v, v, tempOperand);
        }

        public static string GetString(this RegisterAllocator<PTXRegisterKind>.HardwareRegister r)
        {
            return $"%{PTXRegisterAllocator.GetStringRepresentation(r)}";
        }
    }
}
