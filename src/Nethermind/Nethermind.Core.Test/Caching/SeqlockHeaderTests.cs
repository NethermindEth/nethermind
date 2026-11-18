// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Caching;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class SeqlockHeaderTests
{
    [Test]
    public void Header_fields_cover_all_bits_without_overlap()
    {
        long[] fields = [SeqlockHeader.LockMarker, SeqlockHeader.EpochMask, SeqlockHeader.HashMask, SeqlockHeader.SeqMask, SeqlockHeader.OccupiedBit];
        long union = 0;
        int bits = 0;
        foreach (long field in fields)
        {
            Assert.That(union & field, Is.Zero);
            union |= field;
            bits += BitOperations.PopCount((ulong)field);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(union, Is.EqualTo(-1L));
            Assert.That(bits, Is.EqualTo(64));
            Assert.That(BitOperations.PopCount((ulong)SeqlockHeader.SeqMask), Is.EqualTo(26));
            Assert.That(BitOperations.PopCount((ulong)SeqlockHeader.HashMask), Is.EqualTo(20));
        }
    }

    [Test]
    public void Sequence_does_not_repeat_before_two_to_the_26_writes([Values(1L << 16, (1L << 26) - 1)] long writes)
    {
        long header = SeqlockHeader.EpochMask | SeqlockHeader.HashMask | SeqlockHeader.OccupiedBit;
        long seq = header & SeqlockHeader.SeqMask;

        long later = (seq + writes * SeqlockHeader.SeqInc) & SeqlockHeader.SeqMask;

        Assert.That(later, Is.Not.EqualTo(seq));
    }

    [Test]
    public void Sweep_epochs_come_every_quarter_of_the_epoch_range()
    {
        long epoch = 0;
        int sweeps = 0;
        for (int i = 0; i < 1 << 16; i++)
        {
            epoch = (epoch + (1L << SeqlockHeader.EpochShift)) & SeqlockHeader.EpochMask;
            if (SeqlockHeader.IsSweepEpoch(epoch)) sweeps++;
        }

        Assert.That(sweeps, Is.EqualTo(4));
    }

    [Test]
    public void Sequential_eviction_seeds_sample_every_way_about_equally()
    {
        // Evictions seed the sample with per-set stamps that step by one; a skewed sample keeps the stale entries of
        // the ways it skips and evicts newer ones in their place.
        for (long start = 0; start < 20_000; start += 64)
        {
            int[] sampled = new int[8];
            for (int k = 0; k < 32; k++)
            {
                (int a, int b, int c) = SeqlockHeader.Pick3Indices(start + 2 * k);
                sampled[a]++;
                sampled[b]++;
                sampled[c]++;
            }

            // Each way is in about 12 of 32 samples of 3 distinct ways from 8.
            Assert.That(sampled, Is.All.InRange(2, 24), $"32 evictions seeded from {start}");
        }
    }
}
