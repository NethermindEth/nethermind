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
/// Verifies that parallel and serial folds produce identical trees.
/// </summary>
/// <remarks>
/// Covers layouts with boundary masks wider than a machine word.
/// </remarks>
/// <param name="layout"><inheritdoc cref="PbtTilingTests" path="/param[@name='layout']"/></param>
[TestFixture(PbtTrieLayout.FourLevelEveryLevel)]
[TestFixture(PbtTrieLayout.FourLevelInterleaved)]
[TestFixture(PbtTrieLayout.FourLevelBoundaryOnly)]
[TestFixture(PbtTrieLayout.FiveLevelInterleaved)]
[TestFixture(PbtTrieLayout.SixLevelInterleaved)]
[TestFixture(PbtTrieLayout.SixLevelEvery3Depth)]
[TestFixture(PbtTrieLayout.EightLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelEvery4Depth)]
public class ParallelUpdateRootTests(PbtTrieLayout layout)
{
    private const int Workers = 8;

    /// <summary>Repeats each scenario to exercise different thread interleavings.</summary>
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

    /// <summary>Verifies that parallel jobs preserve the production drain's initial bucket bounds.</summary>
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
        }
    }

    /// <summary>Verifies concurrently folded partitions assemble into the same roots and delta as sequential folds.</summary>
    [Test]
    public void ParallelPartitions_LandTheSameTreeAsSequentialPartitions()
    {
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            Random rng = new(repeat);
            List<(byte[] Key, byte[]? Value)> writes = [];
            for (int i = 0; i < 256; i++)
            {
                writes.Add(([.. AccountStem(rng), (byte)rng.Next(256)], Value(rng)));
                writes.Add(([.. CodeStem(rng), (byte)rng.Next(256)], Value(rng)));
                writes.Add(([.. StorageStem(rng), (byte)rng.Next(256)], Value(rng)));
            }

            PbtTreeHarness sequential = new(PooledRefCountingMemoryProvider.Instance, layout);
            PbtTreeHarness parallel = new(PooledRefCountingMemoryProvider.Instance, layout);
            using PbtWriteBatchSet sequentialBatches = Batches(writes);
            using PbtWriteBatchSet parallelBatches = Batches(writes);

            PbtSubtreeStats sequentialDelta = default;
            PbtPartitionRoots sequentialRoots = PbtPartitionRoots.Empty;
            foreach (PbtPartition partition in PbtPartitions.All)
            {
                sequentialRoots = TrieUpdater.UpdateRoot(
                    sequential, sequentialRoots, partition, sequentialBatches[partition], PooledRefCountingMemoryProvider.Instance,
                    layout, concurrency: 1, out PbtSubtreeStats partitionDelta);
                sequentialDelta += partitionDelta;
            }

            PbtPartitionRoots parallelRoots = TrieUpdater.UpdateRoot(
                parallel, PbtPartitionRoots.Empty, parallelBatches, PooledRefCountingMemoryProvider.Instance,
                layout, concurrency: 1, out PbtSubtreeStats parallelDelta);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(parallelRoots.Root, Is.EqualTo(sequentialRoots.Root), $"root mismatch on repeat {repeat}");
                foreach (PbtPartition partition in PbtPartitions.All)
                {
                    Assert.That(parallelRoots[partition], Is.EqualTo(sequentialRoots[partition]), $"{partition} root mismatch on repeat {repeat}");
                }
                Assert.That(parallelDelta, Is.EqualTo(sequentialDelta), $"delta mismatch on repeat {repeat}");
            }
            AssertStoresMatch(sequential, parallel);
        }
    }

    /// <summary>
    /// Verifies parallel updates, deletes, and reinserts over an existing tree.
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

        // Confirm the fold used worker threads.
        Assert.That(mostReadThreads, Is.GreaterThan(1), "no batch was folded by more than one thread");
    }

    /// <summary>
    /// Verifies every buffer rented by a parallel fold is released.
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
    /// Verifies worker exceptions propagate and release leases held by abandoned frames.
    /// </summary>
    [Test]
    public void ParallelFold_RethrowsWhatAWorkerThrewAndReleasesWhatTheAbandonedFramesHeld()
    {
        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, layout) { RootFoldConcurrency = Workers };

        // Seed stored groups so abandoned frames hold leases when the fold throws.
        Random rng = new(3);
        List<(byte[] Key, byte[]? Value)> existing = Writes(rng, accountStems: 2000, contracts: 4, slotsPerContract: 100);
        harness.ApplyBatch(existing);

        // Duplicate stems fail after descent; the batch is large enough to involve a worker.
        using PbtWriteBatch batch = new(estimatedStems: 2048, buckets: null);
        for (int i = 0; i < 2048; i++)
        {
            byte[] stem = AccountStem(rng);
            stem[0] = (byte)(i & 0xF);
            batch.Add(new Stem(stem), PbtStemChanges.Rent().Set(1, new ValueHash256(Value(rng))));
        }

        byte[] duplicateBytes = AccountStem(rng);
        duplicateBytes[0] = 0x05;
        Stem duplicate = new(duplicateBytes);
        batch.Add(duplicate, PbtStemChanges.Rent().Set(1, new ValueHash256(Value(rng))));
        batch.Add(duplicate, PbtStemChanges.Rent().Set(2, new ValueHash256(Value(rng))));

        Assert.That(
            () => TrieUpdater.UpdateRoot(harness, harness.Roots, PbtPartition.Account, batch, provider, layout, Workers, out _),
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
        for (int i = 0; i < accountStems; i++) writes.Add(([.. AccountStem(rng), (byte)rng.Next(256)], Value(rng)));

        for (int contract = 0; contract < contracts; contract++)
        {
            byte[] prefix = StorageStem(rng);
            for (int slot = 0; slot < slotsPerContract; slot++)
            {
                // Vary only the last two bytes so the group branches at depth 240.
                byte[] stem = (byte[])prefix.Clone();
                stem[^2] = (byte)rng.Next(256);
                stem[^1] = (byte)rng.Next(256);
                writes.Add(([.. stem, (byte)rng.Next(256)], Value(rng)));
            }
        }

        return writes;
    }

    private PbtWriteBatchSet Batches(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        using PbtWriteBatchBuilder builder = new();
        foreach ((byte[] key, byte[]? value) in writes)
        {
            ValueHash256 leaf = default;
            value?.CopyTo(leaf.BytesAsSpan);
            builder.SetLeaf(new Stem(key.AsSpan(0, Stem.Length)), key[Stem.Length], leaf);
        }

        return builder.DrainToWriteBatches(layout.Tiling());
    }

    private static byte[] AccountStem(Random rng)
    {
        byte[] stem = new byte[Nethermind.Pbt.Stem.Length];
        rng.NextBytes(stem);
        stem[0] &= 0x0F;
        return stem;
    }

    private static byte[] CodeStem(Random rng)
    {
        byte[] stem = AccountStem(rng);
        stem[0] |= 0x10;
        return stem;
    }

    private static byte[] StorageStem(Random rng)
    {
        byte[] stem = new byte[Nethermind.Pbt.Stem.Length];
        rng.NextBytes(stem);
        stem[0] |= 0x80;
        return stem;
    }

    private static byte[] Value(Random rng)
    {
        byte[] value = new byte[ValueHash256.MemorySize];
        rng.NextBytes(value);
        return value;
    }
}
