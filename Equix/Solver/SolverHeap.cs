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

public unsafe struct Radix
{
    public const int Total = 65536;

    public fixed byte Counts[Total / 2];
    public fixed ushort TempCounts[Total / 2 * 8]; //2^15 buckets with 8 slots each
    public fixed ulong Stage1[Total]; //45 bits
    public fixed ulong Buffer0[16];

    public fixed uint Stage1Indices[Total]; //32bit Stage1
    public fixed uint Buffer1[8];
    public fixed uint Stage2Indices[Total];
    public fixed uint Buffer3[8];
}
