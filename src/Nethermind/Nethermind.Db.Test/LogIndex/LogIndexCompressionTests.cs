// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using static Nethermind.Db.LogIndex.LogIndexStorage;

namespace Nethermind.Db.Test.LogIndex;

[Parallelizable(ParallelScope.All)]
public class LogIndexCompressionTests
{
    // Counts of 2^k - 1 values give a pooled buffer with no spare room after the uncompressed size.
    private static readonly int[] Counts = [1, 2, 15, 63, 114, 127, 255, 1_023, 2_047];

    public static IEnumerable<string> Algorithms() => CompressionAlgorithm.Supported.Keys;

    [Test]
    public void Values_that_do_not_compress_round_trip(
        [ValueSource(nameof(Algorithms))] string algorithmName,
        [ValueSource(nameof(Counts))] int count)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        int[] blocks = Incompressible(count, seed: count);

        byte[] compressed = CompressDbValue(algorithm, [], MemoryMarshal.AsBytes(blocks.AsSpan()));
        int[] decompressed = new int[count + 16];
        DecompressDbValue(algorithm, compressed, decompressed);

        Assert.That(decompressed[..count], Is.EqualTo(blocks));
    }

    [Test]
    public void Decompressing_needs_room_for_the_values_the_decoder_writes_after_the_last_one(
        [ValueSource(nameof(Algorithms))] string algorithmName)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        int[] blocks = Enumerable.Range(1, 100).ToArray();
        byte[] compressed = CompressDbValue(algorithm, [], MemoryMarshal.AsBytes(blocks.AsSpan()));

        Assert.That(() => DecompressDbValue(algorithm, compressed, new int[blocks.Length + 1]), Throws.ArgumentException);
    }

    private static int[] Incompressible(int count, int seed)
    {
        Random random = new(seed);
        int[] blocks = new int[count];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = random.Next(int.MinValue, int.MaxValue);
        // A negative first value would read as the compression marker; 2^28 or more takes TurboPFor's longest first-value form.
        blocks[0] = random.Next(1 << 28, int.MaxValue);
        return blocks;
    }
}
