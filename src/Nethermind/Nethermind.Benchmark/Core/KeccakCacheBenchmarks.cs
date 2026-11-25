// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Benchmarks.Core;

public class KeccakCacheBenchmarks
{
    private byte[] _first = null!;
    private byte[] _second = null!;

    [Params(20, 32, 64)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (AppContext.TryGetSwitch("Nethermind.Core.KeccakCache.Compact", out bool compact) && compact)
            throw new InvalidOperationException("This benchmark requires the default Keccak cache capacity.");

        Random random = new(1729);
        Dictionary<uint, byte[]> inputs = [];
        for (int i = 0; ; i++)
        {
            byte[] input = new byte[Length];
            random.NextBytes(input);
            BinaryPrimitives.WriteInt32LittleEndian(input, i);
            uint bucket = KeccakCache.GetBucket(input);
            if (inputs.TryGetValue(bucket, out byte[] first))
            {
                _first = first;
                _second = input;
                KeccakCache.ComputeTo(_second, out _);
                return;
            }

            inputs.Add(bucket, input);
        }
    }

    [Benchmark]
    public byte Hit()
    {
        KeccakCache.ComputeTo(_second, out ValueHash256 digest);
        return digest.BytesAsSpan[0];
    }

    [Benchmark(OperationsPerInvoke = 2)]
    public byte Miss()
    {
        // The inputs share a bucket. The first lookup evicts the second input and the second
        // restores it, so every lookup misses without resetting the cache in the measured loop.
        KeccakCache.ComputeTo(_first, out _);
        KeccakCache.ComputeTo(_second, out ValueHash256 digest);
        return digest.BytesAsSpan[0];
    }
}
