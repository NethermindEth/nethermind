// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

[TestFixture]
public class SplitBlockFilterTests
{
    private static readonly int[] Counts = [0, 1, 25, 26, 51, 52, 76, 77, 102, 103, 127, 128, 1_000, 100_000];

    [Test]
    public void Contains_every_added_hash_and_rejects_most_others([Values] bool sequentialHashes, [ValueSource(nameof(Counts))] int count)
    {
        SplitBlockFilter filter = new();
        filter.Reset(count);
        Random random = new(42);
        uint[] added = new uint[count];
        for (int i = 0; i < count; i++)
        {
            added[i] = sequentialHashes ? (uint)i : (uint)random.Next();
            filter.Add(added[i]);
        }

        foreach (uint hash in added) Assert.That(filter.MightContain(hash), Is.True);

        const int Probes = 100_000;
        int falsePositives = 0;
        for (int i = 0; i < Probes; i++)
        {
            uint absent = sequentialHashes ? (uint)(count + i) : (uint)random.Next() | 0x8000_0000u;
            if (filter.MightContain(absent)) falsePositives++;
        }

        Assert.That(falsePositives, Is.LessThan(Probes * 2 / 100));
    }

    [Test]
    public void Reset_forgets_hashes_of_the_previous_build()
    {
        SplitBlockFilter filter = new();
        filter.Reset(10_000);
        for (uint i = 0; i < 10_000; i++) filter.Add(i);

        filter.Reset(10);
        filter.Add(5);

        int stale = 0;
        for (uint i = 10; i < 10_000; i++)
        {
            if (filter.MightContain(i)) stale++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.MightContain(5), Is.True);
            Assert.That(stale, Is.LessThan(10_000 * 5 / 100));
        }
    }
}
