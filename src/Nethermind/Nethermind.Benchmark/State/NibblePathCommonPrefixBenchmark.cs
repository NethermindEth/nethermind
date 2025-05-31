// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class NibblePathCommonPrefixBenchmark
{
    private static readonly NibblePath.Key CommonPrefixKey = NibblePath.Key.FromRaw([0xAA, 0xAA, 0xDE, 0xFF]);

    [Benchmark(OperationsPerInvoke = 4)]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(0, 2)]
    [Arguments(1, 2)]
    [Arguments(0, 3)]
    [Arguments(1, 3)]
    [Arguments(0, 4)]
    public int CommonPrefix(int from, int length)
    {
        NibblePath path = NibblePath.FromKey([0xAA, 0xAA]).Slice(from, length);
        NibblePath.Key key = CommonPrefixKey;

        return key.CommonPrefixLength(path) +
               key.CommonPrefixLength(path) +
               key.CommonPrefixLength(path) +
               key.CommonPrefixLength(path);
    }

    [Benchmark(Baseline = true)]
    public int Baseline()
    {
        Span<byte> a = [1, 2, 3];
        Span<byte> b = [1, 2, 4];

        return a.CommonPrefixLength(b);
    }
}
