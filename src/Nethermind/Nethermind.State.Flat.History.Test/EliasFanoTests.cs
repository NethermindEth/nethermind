// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.State.Flat.History.Segmented;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class EliasFanoTests
{
    private static byte[] Encode(ReadOnlySpan<ulong> values)
    {
        byte[] blob = new byte[EliasFano.GetEncodedLength(values)];
        int written = EliasFano.Encode(values, blob);
        Assert.That(written, Is.EqualTo(blob.Length));
        return blob;
    }

    [Test]
    public void Empty_sequence_has_no_predecessor()
    {
        EliasFano.Reader reader = new(Encode(ReadOnlySpan<ulong>.Empty));
        Assert.That(reader.Count, Is.EqualTo(0));
        Assert.That(reader.Predecessor(100, out _, out _), Is.False);
    }

    [Test]
    public void Round_trips_values_by_rank()
    {
        ulong[] values = [5, 20, 30, 31, 1000, 1_000_000];
        EliasFano.Reader reader = new(Encode(values));

        Assert.That(reader.Count, Is.EqualTo(values.Length));
        for (int i = 0; i < values.Length; i++)
            Assert.That(reader[i], Is.EqualTo(values[i]), $"rank {i}");
    }

    // The canonical HistoryStore example: changes at 5, 20, 30.
    [TestCase(3ul, false, 0, 0ul)]      // before the first change
    [TestCase(5ul, true, 0, 5ul)]
    [TestCase(10ul, true, 0, 5ul)]
    [TestCase(19ul, true, 0, 5ul)]
    [TestCase(20ul, true, 1, 20ul)]
    [TestCase(25ul, true, 1, 20ul)]
    [TestCase(30ul, true, 2, 30ul)]
    [TestCase(35ul, true, 2, 30ul)]
    public void Predecessor_matches_floor(ulong query, bool expectedFound, int expectedRank, ulong expectedValue)
    {
        EliasFano.Reader reader = new(Encode([5, 20, 30]));

        bool found = reader.Predecessor(query, out int rank, out ulong value);

        Assert.That(found, Is.EqualTo(expectedFound));
        if (!expectedFound) return;
        Assert.That(rank, Is.EqualTo(expectedRank));
        Assert.That(value, Is.EqualTo(expectedValue));
    }

    [Test]
    public void Single_element()
    {
        EliasFano.Reader reader = new(Encode([42]));
        Assert.That(reader[0], Is.EqualTo(42ul));
        Assert.That(reader.Predecessor(41, out _, out _), Is.False);
        Assert.That(reader.Predecessor(42, out int r0, out ulong v0), Is.True);
        Assert.That((r0, v0), Is.EqualTo((0, 42ul)));
        Assert.That(reader.Predecessor(99, out int r1, out ulong v1), Is.True);
        Assert.That((r1, v1), Is.EqualTo((0, 42ul)));
    }

    [TestCase(1, 3u)]        // dense small universe -> L == 0
    [TestCase(37, 5u)]
    [TestCase(500, 40u)]     // sparse -> L > 0
    [TestCase(2000, 10_000_000u)]
    public void Predecessor_agrees_with_brute_force(int count, uint spread)
    {
        Random rng = new(count * 31 + (int)spread);
        SortedSet<ulong> set = [];
        while (set.Count < count) set.Add((ulong)rng.NextInt64(0, count * (long)spread + 1));
        ulong[] values = [.. set];

        EliasFano.Reader reader = new(Encode(values));
        for (int i = 0; i < values.Length; i++)
            Assert.That(reader[i], Is.EqualTo(values[i]), $"rank {i}");

        ulong max = values[^1];
        for (ulong q = 0; q <= max + 3; q += (max / 257) + 1)
        {
            bool found = reader.Predecessor(q, out int rank, out ulong value);
            (bool eFound, int eRank, ulong eValue) = BruteForce(values, q);
            Assert.That(found, Is.EqualTo(eFound), $"found @ q={q}");
            if (!eFound) continue;
            Assert.That(rank, Is.EqualTo(eRank), $"rank @ q={q}");
            Assert.That(value, Is.EqualTo(eValue), $"value @ q={q}");
        }
    }

    private static (bool found, int rank, ulong value) BruteForce(ulong[] values, ulong query)
    {
        for (int i = values.Length - 1; i >= 0; i--)
            if (values[i] <= query)
                return (true, i, values[i]);
        return (false, -1, 0);
    }
}
