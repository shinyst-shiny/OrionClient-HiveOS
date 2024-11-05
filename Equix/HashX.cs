using DrillX.Compiler;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DrillX
{
    public unsafe class HashX
    {
        public enum RuntimeOption { Interpret, Compile };

        public RuntimeProgram HashXProgram { get; private set; }
        public RuntimeOption Runtime { get; set; } = RuntimeOption.Interpret;
        public SipState RegisterKey { get; private set;}

        private void* _compiledCode;
        public delegate* unmanaged[Cdecl]<ulong*, void> _compiledFunction;

        public static bool TryBuild(byte[] seed, out HashX hashX)
        {
            (SipState key0, SipState key1) = SipState.PairFromSeed(seed);

            var rng = new SipRand(key0);

            hashX = new HashX();

            bool success = hashX.BuildFromRng(rng);
            hashX.RegisterKey = key1;

            return success;
        }

        public static bool TryBuild(byte[] seed, Span<Instruction> instructions, out HashX hashX)
        {
            (SipState key0, SipState key1) = SipState.PairFromSeed(seed);

            var rng = new SipRand(key0);

            hashX = new HashX();

            bool success = hashX.BuildFromRng(rng, instructions);

            hashX.RegisterKey = key1;

            return success;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Emulate(ulong input)
        {
            return HashXProgram.Emulate(RegisterKey, input);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong AsmCompiled(ulong input, ulong* regs)
        {
            return HashXProgram.RunAsmCompiled(RegisterKey, input, _compiledFunction, regs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AsmCompiled_Avx2(ulong input, ulong* regs)
        {
            HashXProgram.RunAsmCompiled_AVX2(RegisterKey, input, _compiledFunction, regs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AsmCompiled_Avx512(ulong input, ulong* regs)
        {
            HashXProgram.RunAsmCompiled_AVX512(RegisterKey, input, _compiledFunction, regs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool InitCompiler()
        {
            return InitCompiler(HashXProgram.Instructions.AsSpan(), (nint)VirtualMemory.HashxVmAlloc(X86Compiler.CodeSize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool InitCompiler(nint programMemory)
        {
            return InitCompiler(HashXProgram.Instructions.AsSpan(), programMemory);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool InitCompiler(Span<Instruction> instructions, nint programMemory)
        {
            _compiledCode = programMemory.ToPointer();

            if (_compiledCode == null)
            {
                return false;
            }

            _compiledFunction = X86Compiler.HashCompileX86(instructions, (byte*)_compiledCode);

            return _compiledCode != null && _compiledFunction != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DestroyCompiler(bool release = false)
        {
            if (release)
            {
                VirtualMemory.HashxVmFree(_compiledCode, X86Compiler.CodeSize);
            }

            _compiledFunction = null;
        }

        private bool BuildFromRng(SipRand rng)
        {
            if(!RuntimeProgram.TryGenerate(rng, out RuntimeProgram program))
            {
                return false;
            }

            HashXProgram = program;

            return true;
        }

        private bool BuildFromRng(SipRand rng, Span<Instruction> instructions)
        {
            if (!RuntimeProgram.TryGenerate(rng, instructions, out RuntimeProgram program))
            {
                return false;
            }

            HashXProgram = program;

            return true;
        }
    }
}
