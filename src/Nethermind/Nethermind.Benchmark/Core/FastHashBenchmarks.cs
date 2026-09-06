// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core;

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class FastHashBenchmarks
{
    private const int OperationsPerInvoke = 1024;
#if !ZK_EVM
    private const long XxHashSeed = 0x510E527FADE682D1L;
#endif
    private byte[] _data = null!;

    [Params(16, 20, 32, 64, 128, 256, 512, 1024)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size * OperationsPerInvoke];
        Random.Shared.NextBytes(_data);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHash()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ReadOnlySpan<byte> input = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref data, i * Size), Size);
            hash = unchecked(hash + input.FastHash());
        }
        return hash;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHashAes()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        Vector128<byte> seed = SpanExtensions.ComputeAesSeed(Size);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            hash = unchecked(hash + SpanExtensions.FastHashAes(ref start, Size, seed));
        }
        return hash;
    }

#if ZK_EVM
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHashCrc()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        uint seed = SpanExtensions.ComputeSeed(Size);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            hash = unchecked(hash + SpanExtensions.FastHashCrc(ref start, Size, seed));
        }
        return hash;
    }
#else
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHashXxHash3()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ReadOnlySpan<byte> input = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref data, i * Size), Size);
            hash = unchecked(hash + SpanExtensions.FastHashXxHash3(input, XxHashSeed));
        }
        return hash;
    }
#endif
}

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class FastHash64Benchmarks
{
    private const int OperationsPerInvoke = 1024;
#if !ZK_EVM
    private const long XxHashSeed = 0x510E527FADE682D1L;
#endif
    private byte[] _data = null!;

    [Params(20, 32)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size * OperationsPerInvoke];
        Random.Shared.NextBytes(_data);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvoke)]
    public long FastHash64()
    {
        long hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            long next = Size == 20
                ? SpanExtensions.FastHash64For20Bytes(ref start)
                : SpanExtensions.FastHash64For32Bytes(ref start);
            hash = unchecked(hash + next);
        }
        return hash;
    }

#if ZK_EVM
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public long FastHash64Crc()
    {
        long hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        uint seed = SpanExtensions.ComputeSeed(Size);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            long next = Size == 20
                ? SpanExtensions.FastHash64For20BytesCrc(ref start, seed)
                : SpanExtensions.FastHash64For32BytesCrc(ref start, seed);
            hash = unchecked(hash + next);
        }
        return hash;
    }
#else
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public long FastHash64XxHash3()
    {
        long hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            hash = unchecked(hash + SpanExtensions.FastHash64XxHash3(ref start, Size, XxHashSeed));
        }
        return hash;
    }
#endif
}
