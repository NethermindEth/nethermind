// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Trie;
using Org.BouncyCastle.Utilities;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Benchmarks.State;

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class NibblePathEqualsBenchmark
{
    private readonly byte[] LongPath1Nibbles;

    private readonly byte[] LongPath2Nibbles;

    private readonly byte[] LongPath1;

    private readonly NibblePath.Key LongPath2;

    public NibblePathEqualsBenchmark()
    {
        LongPath1Nibbles = [
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 1, 2, 3
        ];

        LongPath1 = [
            0x01, 0x23, 0x45, 0x67,
            0x01, 0x23, 0x45, 0x67,
            0x01, 0x23, 0x45, 0x67,
            0x01, 0x23
        ];

        LongPath2Nibbles = LongPath1Nibbles.AsSpan().ToArray();
        LongPath2 = NibblePath.Key.FromNibbles(LongPath1Nibbles);
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public bool Equals()
    {
        NibblePath path = NibblePath.FromKey(LongPath1);

        return path.Equals(LongPath2) &
               path.Equals(LongPath2) &
               path.Equals(LongPath2) &
               path.Equals(LongPath2);
    }

    [Benchmark(Baseline = true)]
    public bool Baseline()
    {
        return Bytes.AreEqual(LongPath1Nibbles, LongPath2Nibbles);
    }
}
