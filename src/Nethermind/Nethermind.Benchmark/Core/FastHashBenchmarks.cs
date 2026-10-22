// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Core;

[ShortRunJob]
[MemoryDiagnoser]
public class TinyTreePathHashBenchmarks
{
    private const int OperationsPerInvoke = 1024;
    private readonly HashAndTinyPath[] _keys = new HashAndTinyPath[OperationsPerInvoke];

    [Params(false, true)]
    public bool WithAddress;

    [GlobalSetup]
    public void Setup()
    {
        GuestHashSeed.Apply();
        Random random = new(42);
        for (int i = 0; i < _keys.Length; i++)
        {
            byte[] bytes = new byte[Hash256.Size];
            random.NextBytes(bytes);
            TinyTreePath path = new(new TreePath(new ValueHash256(bytes), i % (TinyTreePath.MaxNibbleLength + 1)));
            random.NextBytes(bytes);
            _keys[i] = new HashAndTinyPath(WithAddress ? new Hash256(bytes) : null, path);
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int HashAndTinyPath()
    {
        int hash = 0;
        for (int i = 0; i < _keys.Length; i++) hash = unchecked(hash + _keys[i].GetHashCode());
        return hash;
    }
}

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public partial class FastHashBenchmarks
{
    private const int OperationsPerInvoke = 1024;
    private byte[] _data = null!;

    [Params(8, 16, 20, 32, 64, 128, 256, 512, 1024)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size * OperationsPerInvoke];
        Random.Shared.NextBytes(_data);
        GuestHashSeed.Apply();
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

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int FastHashScalar()
    {
        int hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            hash = unchecked(hash + SpanExtensions.FastHashFallback(MemoryMarshal.CreateReadOnlySpan(ref start, Size)));
        }
        return hash;
    }
}

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public partial class FastHash64Benchmarks
{
    private const int OperationsPerInvoke = 1024;
    private byte[] _data = null!;

    [Params(20, 32)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size * OperationsPerInvoke];
        Random.Shared.NextBytes(_data);
        GuestHashSeed.Apply();
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

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public long FastHash64Scalar()
    {
        long hash = 0;
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_data);
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            ref byte start = ref Unsafe.Add(ref data, i * Size);
            long next = Size == 20
                ? SpanExtensions.FastHash64For20BytesFallback(ref start)
                : SpanExtensions.FastHash64For32BytesFallback(ref start);
            hash = unchecked(hash + next);
        }
        return hash;
    }
}

/// <summary>Seeds the hashes the zkEVM build mixes with a run seed, as the guest does before it hashes anything.</summary>
internal static partial class GuestHashSeed
{
    public static void Apply() => Seed();

    static partial void Seed();
}
