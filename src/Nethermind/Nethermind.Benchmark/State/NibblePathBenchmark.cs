// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

public class NibblePathBenchmark
{
    private static readonly byte[][] Nibbles =
    [
        [1],
        [2, 3],
        [2, 3, 4],
        [2, 3, 5, 6],
        [2, 3, 5, 6, 7],
        [2, 3, 5, 6, 7, 8, 9],
        [2, 3, 5, 6, 7, 8, 9, 0xA],
        [2, 3, 5, 6, 7, 8, 9, 0xA, 0xB],
        [2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC],
        [2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD],
        [2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD],
        Enumerable.Repeat(3, 63).Select(b => (byte)b).ToArray()
    ];

    private static readonly NibblePath Long = NibblePath.FromHexString("0x123456789123456789123456789123456789");

    private static readonly (byte[] nibbles, NibblePath path)[] NibblesWithPaths =
        Nibbles
            .Select(n => (n, NibblePath.FromNibbles(n)))
            .ToArray();

    public static IEnumerable<byte[]> GetNibbles() => Nibbles;
    public static IEnumerable<(byte[] nibbles, NibblePath path)> GetNibblesWithPaths() => NibblesWithPaths;

    [Benchmark]
    [ArgumentsSource(nameof(GetNibbles))]
    public NibblePath FromNibbles(byte[] nibbles)
    {
        return NibblePath.FromNibbles(nibbles);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetNibblesWithPaths))]
    public int CommonPrefixLength((byte[] nibbles, NibblePath path) pair)
    {
        return pair.path.CommonPrefixLength(pair.nibbles);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetNibblesWithPaths))]
    public int CommonPrefixLength_ByRef((byte[] nibbles, NibblePath path) pair)
    {
        NibblePath.ByRef byRef = pair.path;
        return byRef.CommonPrefixLength(byRef);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetNibblesWithPaths))]
    public bool Equals((byte[] nibbles, NibblePath path) pair)
    {
        return pair.path.Equals(pair.path);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetNibblesWithPaths))]
    public bool Equals_Ref((byte[] nibbles, NibblePath path) pair)
    {
        NibblePath path = pair.path;
        return ((NibblePath.ByRef)path).Equals(path);
    }

    [Benchmark]
    [Arguments(1, 2)]
    [Arguments(0, 3)]
    [Arguments(0, 30)]
    public NibblePath Slice(int start, int length)
    {
        return Long.Slice(start, length);
    }
}
