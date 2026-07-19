// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class Blake3ManagedTests
{
    /// <summary>Sizes around every structural boundary: block, chunk, and the chaining-value stack merges.</summary>
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

    /// <summary>The BLAKE3 reference test vector for the empty input, as a check independent of the native binding.</summary>
    [Test]
    public void Matches_reference_vector()
    {
        byte[] actual = new byte[32];
        Blake3Managed.Hash([], actual);
        Assert.That(actual.ToHexString(true), Is.EqualTo("0xaf1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"));
    }
}
