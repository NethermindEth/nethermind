// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Buffers;

namespace Nethermind.Benchmarks.Core;

public class SpanSourceBenchmark
{
    private static readonly SpanSource Array = new([1, 2, 3]);
    private static readonly SpanSource CappedArray = new(new CappedArray<byte>([1, 2, 3]));

    [Benchmark]
    public int MemorySize_ByteArray()
    {
        return Array.MemorySize;
    }

    [Benchmark]
    public int MemorySize_CappedArray()
    {
        return CappedArray.MemorySize;
    }

    [Benchmark]
    public int Span_ByteArray()
    {
        Span<byte> span = Array.Span;
        return span[0] + span[1] + span[2];
    }

    [Benchmark]
    public int Span_CappedArray()
    {
        Span<byte> span = CappedArray.Span;
        return span[0] + span[1] + span[2];
    }
}
