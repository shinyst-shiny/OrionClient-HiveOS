using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class SolverHeap
{
    public const int TotalSize = 1897088;

    //65536 hash values + 64 for avx512 registers + 8 for avx512 output
    public const int MaxComputeSolutionsSize = (65536 + 72) * sizeof(ulong);

    public const int FineBucketItems = 12;
    public const int NumFineBuckets = 128;
    public const int CoarseBucketItems = 336;
    public const int NumCoarseBuckets = 256;
}
