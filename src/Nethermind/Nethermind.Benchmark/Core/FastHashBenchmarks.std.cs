// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Core;

public partial class FastHashBenchmarks
{
    private const long XxHashSeed = 0x510E527FADE682D1L;

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHashXxHash3()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ReadOnlySpan<byte> input = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref data, i * Size), Size);
            ulong next = XxHash3.HashToUInt64(input, XxHashSeed);
            hash = unchecked(hash + (int)(next ^ (next >> 32)));
        }
        return hash;
    }
}

public partial class FastHash64Benchmarks
{
    private const long XxHashSeed = 0x510E527FADE682D1L;

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public long FastHash64XxHash3()
    {
        long hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            hash = unchecked(hash + (long)XxHash3.HashToUInt64(MemoryMarshal.CreateReadOnlySpan(ref start, Size), XxHashSeed));
        }
        return hash;
    }
}
