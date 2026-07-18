// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Descending a batch's subtrees over several threads must change nothing but the wall clock: the root,
/// the trie node groups and the leaf blobs must all come out the same as a single-threaded descent's, byte
/// for byte, and the pooled memory must end up as balanced.
/// </summary>
/// <remarks>
/// The batches here are deliberately large and lopsided, a slot descending on a task of its own only once
/// its share of the batch is worth one; the sequences that follow them delete as well as insert, so the
/// collapses and hoists — where a frame writes over what a sibling subtree's frame just settled — run in
/// parallel too.
/// </remarks>
public class TrieUpdaterParallelismTests
{
    private const int Stems = 4000;

    [TestCase(1, 2)]
    [TestCase(1, 16)]
    [TestCase(42, 4)]
    [TestCase(1337, 64)] // more workers than cores: the smallest slot a task takes, and so the deepest fan-out
    public void ParallelDescent_MatchesTheSerialOne(int seed, int parallelism)
    {
        PbtTreeHarness serial = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.Interleaved);
        TrackingMemoryProvider provider = new();
        PbtTreeHarness parallel = new(provider, PbtGroupFormat.Interleaved) { Parallelism = parallelism };

        foreach (List<(byte[] Key, byte[]? Value)> writes in Batches(seed))
        {
            ValueHash256 expected = serial.ApplyBatch(writes);
            Assert.That(parallel.ApplyBatch(writes), Is.EqualTo(expected), "root");
        }

        AssertSameStore(serial, parallel);
        Assert.That(provider.Rented, Is.Not.Empty, "the batches must have rented something to check");
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(TrackingMemoryProvider.CountUnreleased(parallel.HandedOut), Is.Zero, "every buffer the store handed to a read must end up fully released");
    }

    /// <summary>
    /// The bucket bounds a drained batch arrives with are the descent's topmost levels, so a task takes its
    /// range off a table it shares with its siblings rather than off one its own frame partitioned.
    /// </summary>
    [TestCase(7, 8)]
    public void ParallelDescent_OverAPrecalculatedBucketTable_MatchesTheSerialOne(int seed, int parallelism)
    {
        PbtTreeHarness serial = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.EveryLevel);
        PbtTreeHarness parallel = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.EveryLevel) { Parallelism = parallelism };

        foreach (List<(byte[] Key, byte[]? Value)> writes in Batches(seed))
        {
            ValueHash256 expected = serial.ApplyDrainedBatch(writes);
            Assert.That(parallel.ApplyDrainedBatch(writes), Is.EqualTo(expected), "root");
        }

        AssertSameStore(serial, parallel);
    }

    /// <summary>A stem written twice is the producer's error whichever thread the descent runs into it on.</summary>
    [Test]
    public void UnmergedStem_IsRejectedFromATask()
    {
        ValueHash256 value = new(Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111"));

        // The stems all share their first byte and spread evenly over the sixteen slots of the group below
        // it, so that group descends every slot on a task — the duplicate's included.
        using PbtWriteBatch batch = new(estimatedStems: Stems, buckets: null);
        Stem duplicate = new(new byte[31]);
        batch.Add(duplicate, PbtStemChanges.Rent().Set(5, value));
        batch.Add(duplicate, PbtStemChanges.Rent().Set(7, value));
        for (int i = 1; i < Stems; i++)
        {
            byte[] stem = new byte[31];
            stem[1] = (byte)(i % 16 << 4);
            BitConverter.TryWriteBytes(stem.AsSpan(2), i);
            batch.Add(new Stem(stem), PbtStemChanges.Rent().Set(5, value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.EveryLevel);
        Assert.That(
            () => TrieUpdater.UpdateRoot(
                harness, default, batch, PooledRefCountingMemoryProvider.Instance,
                new PbtUpdateOptions(PbtGroupFormat.EveryLevel, Parallelism: 8), out _),
            Throws.InstanceOf<InvalidOperationException>(), "the task's failure reaches the caller as itself");
    }

    private static void AssertSameStore(PbtTreeHarness serial, PbtTreeHarness parallel)
    {
        Assert.That(parallel.Nodes, Has.Count.EqualTo(serial.Nodes.Count), "node count");
        foreach ((TrieNodeKey key, byte[] expected) in serial.Nodes)
        {
            Assert.That(parallel.Nodes.TryGetValue(key, out byte[]? actual), $"missing node at {key}");
            Assert.That(actual.AsSpan().SequenceEqual(expected), $"node mismatch at {key}");
        }

        Assert.That(parallel.Blobs, Has.Count.EqualTo(serial.Blobs.Count), "blob count");
        foreach ((Stem stem, byte[] expected) in serial.Blobs)
        {
            Assert.That(parallel.Blobs.TryGetValue(stem, out byte[]? actual), $"missing blob at {stem}");
            Assert.That(actual.AsSpan().SequenceEqual(expected), $"blob mismatch at {stem}");
        }
    }

    /// <summary>
    /// An insert of many stems, then batches that rewrite, clear and reinsert a moving slice of them.
    /// </summary>
    /// <remarks>
    /// Half the stems are clustered around a handful of bases, which is what makes the top slots uneven
    /// enough for the fan-out to reach past the first level rather than splitting sixteen equal ways.
    /// </remarks>
    private static IEnumerable<List<(byte[] Key, byte[]? Value)>> Batches(int seed)
    {
        Random rng = new(seed);

        List<byte[]> bases = [];
        for (int i = 0; i < 4; i++)
        {
            byte[] baseStem = new byte[31];
            rng.NextBytes(baseStem);
            bases.Add(baseStem);
        }

        List<byte[]> stems = [];
        for (int i = 0; i < Stems; i++)
        {
            byte[] stem;
            if (i % 2 == 0)
            {
                stem = new byte[31];
                rng.NextBytes(stem);
            }
            else
            {
                // a variant of a base, sharing its first bytes: a long shared prefix for the descent to walk
                stem = (byte[])bases[rng.Next(bases.Count)].Clone();
                rng.NextBytes(stem.AsSpan(3 + rng.Next(10)));
            }

            stems.Add(stem);
        }

        List<(byte[], byte[]?)> inserts = [];
        foreach (byte[] stem in stems) inserts.Add(([.. stem, 5], RandomValue(rng)));
        yield return inserts;

        for (int batch = 0; batch < 3; batch++)
        {
            List<(byte[], byte[]?)> writes = [];
            for (int i = batch; i < stems.Count; i += 3)
            {
                // rewrite, add a second sub-index, or delete — the last of which collapses and hoists
                writes.Add(batch switch
                {
                    0 => ([.. stems[i], 5], RandomValue(rng)),
                    1 => ([.. stems[i], 9], RandomValue(rng)),
                    _ => ([.. stems[i], 5], null),
                });
            }

            yield return writes;
        }
    }

    private static byte[] RandomValue(Random rng)
    {
        byte[] value = new byte[32];
        rng.NextBytes(value);
        return value;
    }
}
