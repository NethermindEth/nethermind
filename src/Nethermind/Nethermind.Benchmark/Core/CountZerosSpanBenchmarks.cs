// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmark for <see cref="Bytes.CountZeros(ReadOnlySpan{byte})"/>.
/// Sizes include exact SIMD-width multiples (16, 32, 64) where the off-by-one
/// fix matters, common Ethereum widths (20 = address, 36 = transfer calldata),
/// and larger spans (256, 500, 1024) for throughput scaling.
/// </summary>
public class CountZerosSpanBenchmarks
{
    private byte[] _data = null!;

    [Params(16, 20, 32, 36, 64, 100, 128, 200, 256, 500, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(42);
        _data = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            int roll = rng.Next(10);
            _data[i] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
        }
    }

    [Benchmark]
    public int CountZeros()
        => ((ReadOnlySpan<byte>)_data).CountZeros();
}
