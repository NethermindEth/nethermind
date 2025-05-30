// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Buffers;

namespace Nethermind.Benchmarks.Core;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class TinyArrayBenchmark
{
    private const int MaxLength = 32;

    public static IEnumerable<ISpanSource> Arrays()
    {
        for (int i = 1; i <= MaxLength; i++)
        {
            var source = Enumerable.Range(1, i).Select(i => (byte)i).ToArray();
            yield return TinyArray.Create(source);
        }
    }

    [Benchmark(OperationsPerInvoke = 4)]
    [ArgumentsSource(nameof(Arrays))]
    public bool SequenceEqual(ISpanSource array)
    {
        Span<byte> span = array.Span;

        return array.SequenceEqual(span) &&
               array.SequenceEqual(span) &&
               array.SequenceEqual(span) &&
               array.SequenceEqual(span);
    }

    [Benchmark(OperationsPerInvoke = 4)]
    [ArgumentsSource(nameof(Arrays))]
    public int CommonPrefixLength(ISpanSource array)
    {
        Span<byte> span = array.Span;

        return array.CommonPrefixLength(span) +
               array.CommonPrefixLength(span) +
               array.CommonPrefixLength(span) +
               array.CommonPrefixLength(span);
    }
}
