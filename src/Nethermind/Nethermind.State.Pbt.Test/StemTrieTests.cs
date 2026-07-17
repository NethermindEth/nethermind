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

public class StemTrieTests
{
    [TestCase(3, 1)] // divergence inside the root group: both stems at its boundary slots
    [TestCase(5, 2)] // stems at an inner position of the depth-4 group
    [TestCase(7, 2)] // stems exactly on a group boundary (depth 8)
    [TestCase(8, 3)] // stems just past a group boundary (depth 9)
    [TestCase(10, 3)] // stems mid-group (depth 11)
    [TestCase(247, 62)] // deepest split: stems at the 248-bit level, relocating through every tier
    public void InsertSplitDeleteHoist_MaintainsCanonicalStructureAndRoots(int divergenceBit, int splitGroupCount)
    {
        // stemA and stemB share their first divergenceBit bits and diverge there
        byte[] stemA = new byte[31];
        byte[] stemB = new byte[31];
        stemB[divergenceBit >> 3] = (byte)(1 << (7 - (divergenceBit & 7)));
        byte[] keyA = [.. stemA, 5];
        byte[] keyB = [.. stemB, 7];
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);

        // single stem: one root group holding just the stem
        ValueHash256 root = harness.ApplyBatch([(keyA, valueA)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1));

        // split: a chain of groups down to the stems at their shortest unique prefixes
        root = harness.ApplyBatch([(keyB, valueB)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA), (keyB, valueB)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(splitGroupCount));

        // delete A: B hoists all the way back to the root group, tombstoning the whole chain
        root = harness.ApplyBatch([(keyA, null)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyB, valueB)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1));

        // delete B: empty tree hashes to zero
        root = harness.ApplyBatch([(keyB, null)]);
        Assert.That(root, Is.EqualTo(default(ValueHash256)));
        Assert.That(harness.Nodes, Is.Empty);
    }

    [Test]
    public void UnmergedStem_IsRejectedOnceTheDescentRunsOutOfStem()
    {
        // the producer must merge a stem's writes itself; two entries for one stem partition
        // together all the way to bit 248, where nothing is left to tell them apart
        Stem stem = new(new byte[31]);
        ValueHash256 value = new(Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111"));

        using PbtWriteBatch batch = new(estimatedStems: 2, buckets: null);
        batch.Add(stem, PbtStemChanges.Rent().Set(5, value));
        batch.Add(stem, PbtStemChanges.Rent().Set(7, value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        Assert.That(() => TrieUpdater.UpdateRoot(harness, default, batch, PooledRefCountingMemoryProvider.Instance),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void StemsAtMixedDepths_SplitHoistAndRebuildStayCanonical()
    {
        // A and B diverge deep (bit 20: stems in the depth-20 group, six groups down the shared
        // path); C splits off shallow (bit 6: an inner stem of the depth-4 group, next to the
        // internal path descending towards A and B)
        byte[] stemA = new byte[31];
        byte[] stemB = new byte[31];
        stemB[2] = 0x08;
        byte[] stemC = new byte[31];
        stemC[0] = 0x02;
        byte[] keyA = [.. stemA, 1];
        byte[] keyB = [.. stemB, 2];
        byte[] keyC = [.. stemC, 3];
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] valueC = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        ValueHash256 root = harness.ApplyBatch([(keyA, valueA), (keyB, valueB), (keyC, valueC)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA), (keyB, valueB), (keyC, valueC)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(6));
        AssertStoreMatchesFreshRebuild(harness, [(keyA, valueA), (keyB, valueB), (keyC, valueC)]);

        // delete B: A hoists out of the deep groups and lands next to C at depth 7
        root = harness.ApplyBatch([(keyB, null)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA), (keyC, valueC)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(2));
        AssertStoreMatchesFreshRebuild(harness, [(keyA, valueA), (keyC, valueC)]);

        // delete C then A: hoist to a single root-group stem, then to an empty tree
        root = harness.ApplyBatch([(keyC, null)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1));

        root = harness.ApplyBatch([(keyA, null)]);
        Assert.That(root, Is.EqualTo(default(ValueHash256)));
        Assert.That(harness.Nodes, Is.Empty);
    }

    [Test]
    public void DeleteOnlyBatchOnEmptyTree_AndInPlaceStemUpdate()
    {
        byte[] stemA = new byte[31];
        byte[] stemB = new byte[31];
        stemB[0] = 0x80;
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);

        // deleting absent keys across both root buckets leaves the tree empty
        ValueHash256 root = harness.ApplyBatch([([.. stemA, 5], null), ([.. stemB, 9], null)]);
        Assert.That(root, Is.EqualTo(default(ValueHash256)));
        Assert.That(harness.Nodes, Is.Empty);

        // updating another sub-index of the lone stem folds in place: still a single root group
        root = harness.ApplyBatch([([.. stemA, 5], valueA)]);
        root = harness.ApplyBatch([([.. stemA, 7], valueB)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([([.. stemA, 5], valueA), ([.. stemA, 7], valueB)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1));
    }

    [TestCase(1)]
    [TestCase(42)]
    [TestCase(1337)]
    public void RandomBatches_MatchEipReference_AndStoreStaysCanonical(int seed)
    {
        Random rng = new(seed);

        // a pool of stems with deliberate shared prefixes to exercise splits and hoists
        List<byte[]> stems = [];
        for (int i = 0; i < 8; i++)
        {
            byte[] baseStem = new byte[31];
            rng.NextBytes(baseStem);
            stems.Add(baseStem);
            for (int j = 0; j < 3; j++)
            {
                byte[] variant = (byte[])baseStem.Clone();
                int bit = rng.Next(Stem.LengthInBits);
                variant[bit >> 3] ^= (byte)(1 << (7 - (bit & 7)));
                stems.Add(variant);
            }
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        Dictionary<string, byte[]> model = [];
        for (int batch = 0; batch < 5; batch++)
        {
            List<(byte[], byte[]?)> writes = [];
            for (int i = 0; i < 60; i++)
            {
                byte[] key = [.. stems[rng.Next(stems.Count)], (byte)rng.Next(256)];
                int op = rng.Next(10);
                byte[]? value = null;
                if (op > 1)
                {
                    value = new byte[32];
                    rng.NextBytes(value);
                }
                else if (op == 1)
                {
                    value = new byte[32]; // zero value: cleared like an explicit delete
                }

                writes.Add((key, value));
                string hexKey = key.ToHexString();
                if (value is null || value.AsSpan().IsZero())
                {
                    model.Remove(hexKey);
                }
                else
                {
                    model[hexKey] = value;
                }
            }

            ValueHash256 root = harness.ApplyBatch(writes);
            Assert.That(root, Is.EqualTo(ReferenceRoot(ModelEntries(model))), $"root mismatch after batch {batch}");
        }

        AssertStoreMatchesFreshRebuild(harness, ModelEntries(model));
    }

    [TestCase(7)]
    [TestCase(99)]
    public void LargeSingleBatch_MatchesReference_AndStoreStaysCanonical(int seed)
    {
        Random rng = new(seed);

        // many distinct stems, half of them clustered around a few bases with shared prefixes, so a
        // single batch drives the bulk partition deep and exercises splits across many branches
        List<byte[]> bases = [];
        for (int i = 0; i < 8; i++)
        {
            byte[] baseStem = new byte[31];
            rng.NextBytes(baseStem);
            bases.Add(baseStem);
        }

        List<(byte[], byte[]?)> writes = [];
        Dictionary<string, byte[]> model = [];
        for (int i = 0; i < 300; i++)
        {
            byte[] stem = rng.Next(2) == 0 ? new byte[31] : (byte[])bases[rng.Next(bases.Count)].Clone();
            if (stem.AsSpan().IsZero() || rng.Next(2) == 0) rng.NextBytes(stem);
            else
            {
                int bit = rng.Next(Stem.LengthInBits);
                stem[bit >> 3] ^= (byte)(1 << (7 - (bit & 7)));
            }

            byte[] key = [.. stem, (byte)rng.Next(256)];
            byte[] value = new byte[32];
            rng.NextBytes(value);
            writes.Add((key, value));
            model[key.ToHexString()] = value;
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        ValueHash256 root = harness.ApplyBatch(writes);
        Assert.That(root, Is.EqualTo(ReferenceRoot(ModelEntries(model))));

        AssertStoreMatchesFreshRebuild(harness, ModelEntries(model));
    }

    /// <summary>
    /// A batch drained from the production builder folds to exactly the tree an unordered, hand-built
    /// batch folds to. The drain hands over the bucket bounds for the first two levels instead of
    /// letting the descent derive them, which is only sound if the two agree — and a disagreement
    /// would surface as a silently wrong state root, not as a failure, so both the root and the stored
    /// nodes are compared.
    /// </summary>
    [TestCaseSource(nameof(BucketOrderFixtures))]
    public void DrainedBatch_FoldsIdenticallyToAHandBuiltBatch(byte[][] stems)
    {
        PbtTreeHarness handBuilt = new(PooledRefCountingMemoryProvider.Instance);
        PbtTreeHarness drained = new(PooledRefCountingMemoryProvider.Instance);

        // insert every stem, rewrite every stem, then delete every other one — so the drained path
        // also folds the hoists and blob removals a delete drives, not just inserts
        for (int round = 0; round < 3; round++)
        {
            List<(byte[], byte[]?)> writes = [];
            for (int i = 0; i < stems.Length; i++)
            {
                byte[]? value = null;
                if (round != 2 || i % 2 != 0)
                {
                    value = new byte[32];
                    value.AsSpan().Fill((byte)(round + i + 1));
                }

                writes.Add(([.. stems[i], (byte)(i & 0xFF)], value));
            }

            ValueHash256 expected = handBuilt.ApplyBatch(writes);
            Assert.That(drained.ApplyDrainedBatch(writes), Is.EqualTo(expected), $"root mismatch in round {round}");
            AssertSameNodes(drained, handBuilt, round);
        }
    }

    /// <summary>Stem sets that stress how a drained batch buckets its first two levels.</summary>
    private static IEnumerable<TestCaseData> BucketOrderFixtures()
    {
        // one shard: both levels put the whole batch in a single bucket
        yield return Fixture("a single stem", OneFirstByte(0x80, 1));
        yield return Fixture("one first byte, diverging below depth 8", OneFirstByte(0x80, 20));

        // every shard, so every bucket of both levels is occupied
        byte[][] everyByte = new byte[256][];
        for (int i = 0; i < everyByte.Length; i++) everyByte[i] = MakeStem((byte)i, 0);
        yield return Fixture("every first byte", everyByte);

        // the extremes alone: the longest runs of empty shards, and the widest gap between a byte
        // group's nibble-local ends and the batch-global ones
        yield return Fixture("first bytes at the extremes", [MakeStem(0x00, 0), MakeStem(0xFF, 0)]);

        // empty shards leading, trailing, and inside a nibble group
        byte[] sparse = [0x00, 0x0F, 0x10, 0x7F, 0x80, 0xF0, 0xFF];
        byte[][] sparseStems = new byte[sparse.Length][];
        for (int i = 0; i < sparse.Length; i++) sparseStems[i] = MakeStem(sparse[i], 0);
        yield return Fixture("sparse first bytes", sparseStems);
    }

    private static TestCaseData Fixture(string name, byte[][] stems) => new TestCaseData((object)stems).SetArgDisplayNames(name);

    /// <summary><paramref name="count"/> distinct stems that all share <paramref name="firstByte"/>, so they diverge only below depth 8.</summary>
    private static byte[][] OneFirstByte(byte firstByte, int count)
    {
        byte[][] stems = new byte[count][];
        for (int i = 0; i < count; i++) stems[i] = MakeStem(firstByte, (byte)i);
        return stems;
    }

    /// <summary>A stem in the shard <paramref name="firstByte"/> keys, told apart from its shard-mates by <paramref name="tail"/>.</summary>
    private static byte[] MakeStem(byte firstByte, byte tail)
    {
        byte[] stem = new byte[Stem.Length];
        stem[0] = firstByte;
        stem[^1] = tail;
        return stem;
    }

    private static void AssertSameNodes(PbtTreeHarness actual, PbtTreeHarness expected, int round)
    {
        Assert.That(actual.Nodes.Keys, Is.EquivalentTo(expected.Nodes.Keys), $"node set mismatch in round {round}");
        foreach ((TrieNodeKey key, byte[] node) in expected.Nodes)
        {
            Assert.That(actual.Nodes[key], Is.EqualTo(node), $"node {key} differs in round {round}");
        }
    }

    /// <summary>
    /// Every buffer the updater rents is released, whichever way the descent settles a group: the
    /// ones handed to the store are released by the store, the rest by the updater. A leak would
    /// silently starve the array pool, and an over-release would hand one buffer to two owners.
    /// </summary>
    [Test]
    public void UpdateRoot_BalancesTheLeasesOnEveryBufferItRents()
    {
        byte[] stemA = new byte[31];
        byte[] stemB = new byte[31];
        stemB[20] = 0x01; // diverges deep, so the split relocates across several group boundaries
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider);

        // each batch settles groups by a different path: fresh groups, an unchanged rebuild that is
        // dropped rather than written, a split that rewrites the chain, and a delete that removes
        // groups and hoists the survivor back up
        harness.ApplyBatch([([.. stemA, 5], valueA)]);
        harness.ApplyBatch([([.. stemA, 5], valueA)]);
        harness.ApplyBatch([([.. stemB, 7], valueB)]);
        harness.ApplyBatch([([.. stemA, 5], null)]);

        Assert.That(provider.Rented, Is.Not.Empty, "the batches must have rented something to check");
        Assert.That(CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(CountUnreleased(harness.HandedOut), Is.Zero, "every buffer the store handed to a read must end up fully released");
    }

    /// <summary>
    /// How many of <paramref name="memories"/> still hold a lease — none should, once the updater has
    /// returned. A fully released buffer refuses a fresh lease, an outstanding one takes it.
    /// </summary>
    private static int CountUnreleased(IEnumerable<RefCountingMemory> memories)
    {
        int unreleased = 0;
        foreach (RefCountingMemory memory in memories)
        {
            try
            {
                memory.AcquireLease();
            }
            catch (InvalidOperationException)
            {
                continue; // refused: fully released, which is what we want to see
            }

            ((IDisposable)memory).Dispose();
            unreleased++;
        }

        return unreleased;
    }

    /// <summary>Hands out pooled memory and keeps a handle on each buffer, for <see cref="CountUnreleased"/>.</summary>
    private sealed class TrackingMemoryProvider : IRefCountingMemoryProvider
    {
        private readonly List<RefCountingMemory> _rented = [];

        public IReadOnlyList<RefCountingMemory> Rented => _rented;

        public RefCountingMemory Rent(int length)
        {
            RefCountingMemory memory = PooledRefCountingMemoryProvider.Instance.Rent(length);
            _rented.Add(memory);
            return memory;
        }
    }

    /// <summary>
    /// The incrementally built store must be byte-identical to a from-scratch rebuild of the
    /// surviving entries: canonical structure, no stale or missing groups.
    /// </summary>
    private static void AssertStoreMatchesFreshRebuild(PbtTreeHarness harness, IEnumerable<(byte[] Key, byte[]? Value)> survivingEntries)
    {
        PbtTreeHarness fresh = new(PooledRefCountingMemoryProvider.Instance);
        fresh.ApplyBatch(survivingEntries);
        Assert.That(harness.Nodes, Has.Count.EqualTo(fresh.Nodes.Count));
        foreach ((TrieNodeKey key, byte[] expected) in fresh.Nodes)
        {
            Assert.That(harness.Nodes.TryGetValue(key, out byte[]? actual), $"missing node at {key}");
            Assert.That(actual.AsSpan().SequenceEqual(expected), $"node mismatch at {key}");
        }
    }

    private static List<(byte[], byte[]?)> ModelEntries(Dictionary<string, byte[]> model)
    {
        List<(byte[], byte[]?)> entries = [];
        foreach ((string key, byte[] value) in model)
        {
            entries.Add((Bytes.FromHexString(key), value));
        }

        return entries;
    }

    private static ValueHash256 ReferenceRoot(IEnumerable<(byte[] Key, byte[]? Value)> entries)
    {
        EipReferenceTree reference = new();
        foreach ((byte[] key, byte[]? value) in entries)
        {
            if (value is not null) reference.Insert(key, value);
        }

        return new ValueHash256(reference.Merkelize());
    }
}
