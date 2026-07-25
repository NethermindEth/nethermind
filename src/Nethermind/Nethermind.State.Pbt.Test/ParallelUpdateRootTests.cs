// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// A batch folded across several threads must land the same tree, byte for byte, as the same batch
/// folded on one. Nothing about the result is allowed to depend on which thread took which bucket, so
/// every assertion here compares a parallel fold against a serial one over the same writes.
/// </summary>
/// <remarks>
/// Run under every layout: a job settles its result into its parent's boundary by slot, so a tiling
/// whose boundary is wider than a machine word is what a slot-indexed mask has to be right about.
/// </remarks>
/// <param name="layout"><inheritdoc cref="PbtTilingTests" path="/param[@name='layout']"/></param>
[TestFixture(PbtTrieLayout.ClusteredFourLevelEveryLevel)]
[TestFixture(PbtTrieLayout.ClusteredFourLevelInterleaved)]
[TestFixture(PbtTrieLayout.ClusteredFourLevelBoundaryOnly)]
[TestFixture(PbtTrieLayout.SixLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelEvery4Depth)]
public class ParallelUpdateRootTests(PbtTrieLayout layout)
{
    private const int Workers = 8;

    /// <summary>Repeats of each scenario: the interleaving differs run to run, so one pass proves little.</summary>
    private const int Repeats = 8;

    /// <param name="accountStems">Stems spread over the whole key space, which branch at the topmost levels — where the fold spawns first.</param>
    /// <param name="contracts">Groups of stems sharing a long prefix, as one contract's storage does: they descend a run before they branch, so their buckets only split deep.</param>
    /// <param name="slotsPerContract">Stems in each of those groups.</param>
    [TestCase(4000, 0, 0, TestName = "accounts only")]
    [TestCase(0, 8, 400, TestName = "storage corridors only")]
    [TestCase(1200, 16, 100, TestName = "accounts and storage")]
    [TestCase(1030, 0, 0, TestName = "just past the parallel threshold")]
    public void ParallelFold_LandsTheSameTreeAsTheSerialOne(int accountStems, int contracts, int slotsPerContract)
    {
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            Random rng = new(repeat);
            List<(byte[] Key, byte[]? Value)> writes = Writes(rng, accountStems, contracts, slotsPerContract);

            PbtTreeHarness serial = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = 1 };
            PbtTreeHarness parallel = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = Workers };

            Assert.That(parallel.ApplyBatch(writes), Is.EqualTo(serial.ApplyBatch(writes)), $"root mismatch on repeat {repeat}");
            AssertStoresMatch(serial, parallel);
        }
    }

    /// <summary>
    /// The production drain hands the fold the bucket bounds for its first two levels, and the first is
    /// exactly where it spawns: a job carries that level over as a range of the batch's bucket table
    /// rather than as the span the frame held, so a wrong range would fold the wrong entries.
    /// </summary>
    [Test]
    public void ParallelFold_OfADrainedBatch_LandsTheSameTreeAsTheSerialOne()
    {
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            Random rng = new(repeat);
            List<(byte[] Key, byte[]? Value)> writes = Writes(rng, accountStems: 1000, contracts: 8, slotsPerContract: 200);

            PbtTreeHarness serial = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = 1 };
            PbtTreeHarness parallel = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = Workers };

            Assert.That(parallel.ApplyDrainedBatch(writes), Is.EqualTo(serial.ApplyDrainedBatch(writes)), $"root mismatch on repeat {repeat}");
            AssertStoresMatch(serial, parallel);
            Assert.That(serial.ReadThreadCount, Is.EqualTo(1), "a serial fold reads from the calling thread alone");
        }
    }

    /// <summary>
    /// The same over a sequence of batches: updates in place, deletes that collapse groups and dissolve
    /// runs, and re-inserts. A fold over a stored tree reads and rewrites where the first one only
    /// wrote, so this is what pins that a worker's reads and its buffered writes stay in step.
    /// </summary>
    [Test]
    public void ParallelFold_OverASequenceOfBatches_LandsTheSameTreeAsTheSerialOne()
    {
        int mostReadThreads = 0;
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            Random rng = new(repeat);
            PbtTreeHarness serial = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = 1 };
            PbtTreeHarness parallel = new(PooledRefCountingMemoryProvider.Instance, layout) { RootFoldConcurrency = Workers };

            List<(byte[] Key, byte[]? Value)> live = Writes(rng, accountStems: 2000, contracts: 8, slotsPerContract: 100);
            serial.ApplyBatch(live);
            parallel.ApplyBatch(live);

            for (int batch = 0; batch < 3; batch++)
            {
                List<(byte[] Key, byte[]? Value)> writes = [];
                foreach ((byte[] key, byte[]? value) in live)
                {
                    switch (rng.Next(4))
                    {
                        case 0: writes.Add((key, null)); break; // delete
                        case 1: writes.Add((key, Value(rng))); break; // rewrite
                        case 2: writes.Add((key, value)); break; // no-op rewrite
                    }
                }

                Assert.That(parallel.ApplyBatch(writes), Is.EqualTo(serial.ApplyBatch(writes)), $"root mismatch on repeat {repeat}, batch {batch}");
                AssertStoresMatch(serial, parallel);
            }

            mostReadThreads = Math.Max(mostReadThreads, parallel.ReadThreadCount);
        }

        // the comparison above is only worth something if the fold really did leave the calling thread
        Assert.That(mostReadThreads, Is.GreaterThan(1), "no batch was folded by more than one thread");
    }

    /// <summary>
    /// Every buffer a parallel fold rents is released, whichever worker rented it: a fold that leaked one
    /// per bucket would starve the pool a thread at a time.
    /// </summary>
    [Test]
    public void ParallelFold_BalancesTheLeasesOnEveryBufferItRents()
    {
        Random rng = new(17);
        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, layout) { RootFoldConcurrency = Workers };

        List<(byte[] Key, byte[]? Value)> live = Writes(rng, accountStems: 2000, contracts: 8, slotsPerContract: 100);
        harness.ApplyBatch(live);
        harness.ApplyBatch(live);

        List<(byte[] Key, byte[]? Value)> deletes = [];
        foreach ((byte[] key, _) in live) deletes.Add((key, null));
        harness.ApplyBatch(deletes);

        Assert.That(harness.Nodes, Is.Empty, "the deletes must have emptied the tree");
        Assert.That(provider.Rented, Is.Not.Empty, "the batches must have rented something to check");
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(TrackingMemoryProvider.CountUnreleased(harness.HandedOut), Is.Zero, "every buffer the store handed to a read must end up fully released");
    }

    /// <summary>
    /// A fold that throws on a worker's bucket must surface that on the calling thread — and must give
    /// back every lease the abandoned frames were holding, rather than leaving the buffers behind them
    /// to the garbage collector. The frames unwound by the throw are the ones the descent never settles,
    /// so nothing but the unwinding itself can release what their boundary slots and their outstanding
    /// buckets hold.
    /// </summary>
    [Test]
    public void ParallelFold_RethrowsWhatAWorkerThrewAndReleasesWhatTheAbandonedFramesHeld()
    {
        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, layout) { RootFoldConcurrency = Workers };

        // a first batch so that the throwing fold descends stored groups, whose blobs the frames it
        // abandons are holding — a fold over an empty tree reads nothing and would prove less
        Random rng = new(3);
        List<(byte[] Key, byte[]? Value)> existing = Writes(rng, accountStems: 2000, contracts: 4, slotsPerContract: 100);
        ValueHash256 root = harness.ApplyBatch(existing);

        // one stem added twice, which the descent rejects once it has consumed the whole stem; the batch
        // is big enough that the bucket holding it is likely to be one a worker took
        using PbtWriteBatch batch = new(estimatedStems: 2048, buckets: null);
        for (int i = 0; i < 2048; i++)
        {
            batch.Add(new Stem(Stem(rng)), PbtStemChanges.Rent().Set(1, new ValueHash256(Value(rng))));
        }

        Stem duplicate = new(Stem(rng));
        batch.Add(duplicate, PbtStemChanges.Rent().Set(1, new ValueHash256(Value(rng))));
        batch.Add(duplicate, PbtStemChanges.Rent().Set(2, new ValueHash256(Value(rng))));

        Assert.That(
            () => TrieUpdater.UpdateRoot(harness, root, batch, provider, layout, Workers, out _),
            Throws.InstanceOf<InvalidOperationException>());

        Assert.That(provider.Rented, Is.Not.Empty, "the fold must have rented something to check");
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(TrackingMemoryProvider.CountUnreleased(harness.HandedOut), Is.Zero, "every buffer the store handed to a read must end up fully released");
    }

    private static void AssertStoresMatch(PbtTreeHarness serial, PbtTreeHarness parallel)
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

    /// <inheritdoc cref="ParallelFold_LandsTheSameTreeAsTheSerialOne" path="/param"/>
    private static List<(byte[] Key, byte[]? Value)> Writes(Random rng, int accountStems, int contracts, int slotsPerContract)
    {
        List<(byte[] Key, byte[]? Value)> writes = [];
        for (int i = 0; i < accountStems; i++) writes.Add(([.. Stem(rng), (byte)rng.Next(256)], Value(rng)));

        for (int contract = 0; contract < contracts; contract++)
        {
            byte[] prefix = Stem(rng);
            for (int slot = 0; slot < slotsPerContract; slot++)
            {
                // the last two bytes vary, so the group parts only at depth 240 and the whole contract
                // descends one run to get there
                byte[] stem = (byte[])prefix.Clone();
                stem[^2] = (byte)rng.Next(256);
                stem[^1] = (byte)rng.Next(256);
                writes.Add(([.. stem, (byte)rng.Next(256)], Value(rng)));
            }
        }

        return writes;
    }

    private static byte[] Stem(Random rng)
    {
        byte[] stem = new byte[Nethermind.Pbt.Stem.Length];
        rng.NextBytes(stem);
        return stem;
    }

    private static byte[] Value(Random rng)
    {
        byte[] value = new byte[ValueHash256.MemorySize];
        rng.NextBytes(value);
        return value;
    }
}
