using DrillX;
using DrillX.Compiler;
using DrillX.Solver;
using Equix;
using NLog;
using Org.BouncyCastle.Crypto.Signers;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Hashers.CPU
{
    public unsafe class HybridCPUHasherAVX2 : BaseCPUHasher
    {
        public override string Name => "Hybrid AVX2";
        public override string Description => "C#/C Drillx implementation with minor AVX2 optimizations";

        protected override void ExecuteThread(Tuple<int, int> range, ParallelLoopState loopState, ConcurrentQueue<Exception> exceptions)
        {
            if (!_solverQueue.TryDequeue(out Solver solver))
            {
                return;
            }

            try
            {
                int bestDifficulty = 0;
                byte[] bestSolution = null;
                ulong bestNonce = 0;


                byte[] fullChallenge = new byte[40];
                Span<byte> fullChallengeSpan = new Span<byte>(fullChallenge);
                EquixSolution* solutions = stackalloc EquixSolution[EquixSolution.MaxLength];
                Span<ushort> testSolution = stackalloc ushort[8];
                Span<byte> hashOutput = stackalloc byte[32];

                _info.Challenge.AsSpan().CopyTo(fullChallenge);

                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (i % 32 == 0)
                    {
                        if (ResettingChallenge)
                        {
                            return;
                        }
                    }

                    ulong currentNonce = (ulong)i + _info.CurrentNonce;

                    BinaryPrimitives.WriteUInt64LittleEndian(fullChallengeSpan.Slice(32), currentNonce);

                    //Verifies challenge is valid
                    if (!HashX.TryBuild(fullChallenge, solver.ProgramCache, out HashX program))
                    {
                        continue;
                    }

                    if (!program.InitCompiler(solver.ProgramCache, solver.CompiledProgram))
                    {
                        continue;
                    }

                    int solutionCount = solver.Solve_Avx2_C(program, solutions, solver.Heap, solver.ComputeSolutions);

                    program.DestroyCompiler();

                    //Calculate difficulty
                    for (int z = 0; z < solutionCount; z++)
                    {
                        Span<EquixSolution> s = new Span<EquixSolution>(solutions, EquixSolution.MaxLength);

                        Span<ushort> eSolution = MemoryMarshal.Cast<EquixSolution, ushort>(s.Slice(z, 1));
                        eSolution.CopyTo(testSolution);

                        testSolution.Sort();

                        SHA3.Sha3Hash(testSolution, currentNonce, hashOutput);

                        int difficulty = CalculateTarget(hashOutput);

                        if (difficulty > bestDifficulty)
                        {
                            //Makes sure the ordering for the solution is valid
                            Reorder(s.Slice(z, 1));

                            bestDifficulty = difficulty;
                            bestNonce = currentNonce;
                            bestSolution = MemoryMarshal.Cast<ushort, byte>(eSolution).ToArray();
                        }
                    }

                    _info.AddSolutionCount((ulong)solutionCount);
                }

                _info.UpdateDifficulty(bestDifficulty, bestSolution, bestNonce);
            }
            catch (Exception e)
            {
                exceptions.Enqueue(e);
            }
            finally
            {
                _solverQueue.Enqueue(solver);
            }
        }

        public override bool IsSupported()
        {
            return Avx2.IsSupported && HasNativeFile();
        }
    }
}
