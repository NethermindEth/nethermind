// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Compares the shared hash cache with hashing directly, for inputs that repeat and inputs that never repeat.
/// </summary>
/// <remarks>
/// The difference between <see cref="CachedUnique"/> and <see cref="DirectUnique"/> is what a miss adds;
/// the difference between <see cref="DirectUnique"/> and <see cref="CachedRepeated"/> is what a hit saves.
/// Both give the hit rate above which the cache pays for itself.
/// </remarks>
[MemoryDiagnoser]
public class KeccakCacheShapeBenchmarks
{
    private const int Operations = 1024;
    private readonly byte[] _input = new byte[64];
    private ulong _counter;

    [Params(32, 64)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup() => new Random(42).NextBytes(_input);

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    public ValueHash256 DirectUnique()
    {
        ValueHash256 result = default;
        ReadOnlySpan<byte> input = _input.AsSpan(0, Length);
        for (int i = 0; i < Operations; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_input, ++_counter);
            result = ValueKeccak.Compute(input);
        }
        return result;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public ValueHash256 CachedUnique()
    {
        ValueHash256 result = default;
        ReadOnlySpan<byte> input = _input.AsSpan(0, Length);
        for (int i = 0; i < Operations; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_input, ++_counter);
            KeccakCache.ComputeTo(input, out result);
        }
        return result;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public ValueHash256 CachedRepeated()
    {
        ValueHash256 result = default;
        ReadOnlySpan<byte> input = _input.AsSpan(0, Length);
        for (int i = 0; i < Operations; i++)
        {
            KeccakCache.ComputeTo(input, out result);
        }
        return result;
    }
}
