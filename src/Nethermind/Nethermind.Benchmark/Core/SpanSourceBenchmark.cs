// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Buffers;

namespace Nethermind.Benchmarks.Core;

[DisassemblyDiagnoser(maxDepth: 2)]
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
    public Span<byte> Span_ByteArray()
    {
        return Array.Span;
    }

    [Benchmark]
    public Span<byte> Span_CappedArray()
    {
        return CappedArray.Span;
    }
}
