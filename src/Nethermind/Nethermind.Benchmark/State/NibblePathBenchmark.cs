// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class NibblePathBenchmark
{
    private static readonly byte[][] Nibbles =
    [
        [1],
        [2, 3],
        //[2, 3, 4],
        [2, 3, 5, 6],
        //[2, 3, 5, 6, 7],
        [2, 3, 5, 6, 7, 8, 9],
        //[2, 3, 5, 6, 7, 8, 9, 0xA],
        //[2, 3, 5, 6, 7, 8, 9, 0xA, 0xB],
        //[2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC],
        //[2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD],
        //[2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD, 2, 3, 5, 6, 7, 8, 9, 0xA, 0xB, 0xC, 0xD],
        Enumerable.Repeat(3, 63).Select(b => (byte)b).ToArray()
    ];

    private static readonly NibblePath.Key
        Long = NibblePath.Key.FromHexString("0x123456789123456789123456789123456789");

    private static readonly NibblePath.Key[] NibblesWithPaths =
        Nibbles
            .Select(n => NibblePath.Key.FromNibbles(n))
            .ToArray();

    public static IEnumerable<byte[]> GetNibbles() => Nibbles;
    public static IEnumerable<NibblePath.Key> GetKeys() => NibblesWithPaths;

    [Benchmark]
    [ArgumentsSource(nameof(GetNibbles))]
    public NibblePath.Key FromNibbles(byte[] nibbles)
    {
        return NibblePath.Key.FromNibbles(nibbles);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetKeys))]
    public int CommonPrefixLength_Key(NibblePath.Key key)
    {
        NibblePath path = key.AsPath();

        return key.CommonPrefixLength(path);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetKeys))]
    public int CommonPrefixLength_Path(NibblePath.Key key)
    {
        NibblePath path = key.AsPath();

        return path.CommonPrefixLength(path);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetKeys))]
    public bool Equals_Path_Key(NibblePath.Key key)
    {
        NibblePath path = key.AsPath();

        return path.Equals(key);
    }

    [Benchmark]
    [ArgumentsSource(nameof(GetKeys))]
    public bool Equals_Key_Key(NibblePath.Key key)
    {
        return key.Equals(key);
    }

    [Benchmark]
    [Arguments(1, 2)]
    [Arguments(0, 3)]
    [Arguments(0, 30)]
    public NibblePath Slice_Path(int start, int length)
    {
        NibblePath path = Long.AsPath();
        return path.Slice(start, length);
    }
}
