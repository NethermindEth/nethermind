// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class Blake3ManagedTests
{
    /// <summary>Sizes around BLAKE3 block, chunk, and chaining-value stack boundaries.</summary>
    private static IEnumerable<int> Sizes()
    {
        foreach (int size in new[] { 0, 1, 31, 32, 33, 63, 64, 65, 127, 128, 1023, 1024, 1025, 2048, 2049, 3072, 4096, 5000, 8192, 100_000 })
            yield return size;
    }

    [TestCaseSource(nameof(Sizes))]
    public void Matches_native_blake3(int size)
    {
        byte[] input = new byte[size];
        new Random(size).NextBytes(input);

        byte[] expected = new byte[32];
        global::Blake3.Hasher.Hash(input, expected);

        byte[] actual = new byte[32];
        Blake3Managed.Hash(input, actual);

        Assert.That(actual.ToHexString(), Is.EqualTo(expected.ToHexString()));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Pair_matches_native_blake3(bool lowIsZero, bool highIsZero)
    {
        byte[] pair = new byte[64];
        new Random(1).NextBytes(pair);
        if (lowIsZero) pair.AsSpan(0, 32).Clear();
        if (highIsZero) pair.AsSpan(32, 32).Clear();

        byte[] expected = new byte[32];
        global::Blake3.Hasher.Hash(pair, expected);

        byte[] actual = new byte[32];
        Blake3Managed.HashPair(pair.AsSpan(0, 32), pair.AsSpan(32, 32), actual);

        Assert.That(actual.ToHexString(), Is.EqualTo(expected.ToHexString()));
    }

    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void Compact_fold_matches_scalar_tree_for_all_presence_masks(int width)
    {
        ValueHash256[] sources = new ValueHash256[width];
        ValueHash256[] level = new ValueHash256[width];
        byte[] slicedBuffer = new byte[4 + width * ValueHash256.Length + 7];
        for (int source = 0; source < sources.Length; source++)
        {
            byte[] bytes = new byte[ValueHash256.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(source + 17 * i + 1);
            sources[source] = new ValueHash256(bytes);
        }

        int masks = 1 << width;
        for (int presenceMask = 0; presenceMask < masks; presenceMask++)
        {
            slicedBuffer.AsSpan().Fill(0xA5);
            int written = 0;
            for (int source = 0; source < sources.Length; source++)
            {
                bool present = (presenceMask & (1 << source)) != 0;
                level[source] = present ? sources[source] : default;
                if (present)
                {
                    sources[source].Bytes.CopyTo(slicedBuffer.AsSpan(4 + written));
                    written += ValueHash256.Length;
                }
            }

            for (int levelWidth = width; levelWidth > 1; levelWidth /= 2)
            {
                for (int pair = 0; pair < levelWidth / 2; pair++)
                {
                    level[pair] = Blake3Hash.HashPairOrZero(level[2 * pair], level[2 * pair + 1]);
                }
            }

            ReadOnlySpan<byte> compactSources = slicedBuffer.AsSpan(4, written);
            ValueHash256 actual = width switch
            {
                4 => Blake3Hash.FoldFour(compactSources, (byte)presenceMask),
                8 => Blake3Hash.FoldEight(compactSources, (byte)presenceMask),
                _ => Blake3Hash.FoldSixteen(compactSources, (ushort)presenceMask),
            };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual, Is.EqualTo(level[0]), $"mask 0x{presenceMask:X}");
                Assert.That(slicedBuffer.AsSpan(0, 4).IndexOfAnyExcept((byte)0xA5), Is.EqualTo(-1), $"prefix at mask 0x{presenceMask:X}");
                Assert.That(slicedBuffer.AsSpan(4 + written).IndexOfAnyExcept((byte)0xA5), Is.EqualTo(-1), $"suffix at mask 0x{presenceMask:X}");
            }
        }
    }

    /// <summary>Verifies the BLAKE3 empty-input reference vector independently of the native binding.</summary>
    [Test]
    public void Matches_reference_vector()
    {
        byte[] actual = new byte[32];
        Blake3Managed.Hash([], actual);
        Assert.That(actual.ToHexString(true), Is.EqualTo("0xaf1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"));
    }
}
