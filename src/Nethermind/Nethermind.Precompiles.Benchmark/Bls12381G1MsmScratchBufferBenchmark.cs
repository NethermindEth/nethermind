// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;

namespace Nethermind.Precompiles.Benchmark;

/// <summary>
/// Isolates the allocation/clear change made to <c>Bls12381G1MsmPrecompile.Msm</c>: the per-call
/// scratch buffers (<c>rawPoints</c> = nItems·G1.Sz longs, <c>rawScalars</c> = nItems·32 bytes) were
/// switched from a zero-clearing <see cref="ArrayPoolList{T}"/> (a heap-allocated wrapper) to a
/// non-clearing <see cref="ArrayPoolSpan{T}"/> (a struct). The decode overwrites every slot before
/// MultiMult reads it, so the clear is pure waste. Both methods do identical fill work; the delta is
/// the wasted clear plus the two wrapper allocations.
/// </summary>
[MemoryDiagnoser]
public class Bls12381G1MsmScratchBufferBenchmark
{
    // Nethermind.Crypto.Bls.P1.Sz — longs per decoded G1 point slot.
    private const int G1Sz = 96;

    [Params(16, 256, 2048)]
    public int NItems;

    private int _pointLongs;
    private int _scalarBytes;

    [GlobalSetup]
    public void Setup()
    {
        _pointLongs = NItems * G1Sz;
        _scalarBytes = NItems * 32;

        // Warm the shared pool so Rent returns pooled arrays during measurement — this keeps the
        // reported Allocated delta attributable to the wrapper objects, not one-off pool growth.
        for (int i = 0; i < 8; i++)
        {
            long[] p = ArrayPool<long>.Shared.Rent(_pointLongs);
            byte[] s = ArrayPool<byte>.Shared.Rent(_scalarBytes);
            ArrayPool<long>.Shared.Return(p);
            ArrayPool<byte>.Shared.Return(s);
        }
    }

    [Benchmark(Baseline = true, Description = "ArrayPoolList + clear (before)")]
    public long Old_ArrayPoolList_WithClear()
    {
        using ArrayPoolList<long> rawPoints = new(_pointLongs, _pointLongs);
        using ArrayPoolList<byte> rawScalars = new(_scalarBytes, _scalarBytes);
        return Fill(rawPoints.AsSpan(), rawScalars.AsSpan());
    }

    [Benchmark(Description = "ArrayPoolSpan, no clear (after)")]
    public long New_ArrayPoolSpan_NoClear()
    {
        using ArrayPoolSpan<long> rawPoints = new(_pointLongs);
        using ArrayPoolSpan<byte> rawScalars = new(_scalarBytes);
        return Fill(rawPoints, rawScalars);
    }

    // Simulates the decode pass fully overwriting every scratch slot (identical work in both paths).
    private static long Fill(Span<long> points, Span<byte> scalars)
    {
        points.Fill(1);
        scalars.Fill(1);
        return points.Length + scalars.Length + points[^1] + scalars[^1];
    }
}
