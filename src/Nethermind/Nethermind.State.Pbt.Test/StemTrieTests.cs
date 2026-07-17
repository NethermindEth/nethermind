// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class StemTrieTests
{
    // The store after the split is the root group, the group holding the divergence, and — once they are
    // more than four levels apart — one chain spanning everything between, however deep that reaches.
    [TestCase(3, 1)] // divergence inside the root group: both stems at its boundary slots
    [TestCase(5, 2)] // stems at an inner position of the depth-4 group
    [TestCase(7, 2)] // stems exactly on a group boundary (depth 8)
    [TestCase(8, 3)] // stems just past a group boundary (depth 9): the chain appears, spanning depth 4 to 8
    [TestCase(10, 3)] // stems mid-group (depth 11)
    [TestCase(163, 3)] // deep divergence just inside a group (depth 160)
    [TestCase(164, 3)] // and just past that boundary (depth 164), which only lengthens the chain
    [TestCase(247, 3)] // deepest split: stems at the 248-bit level, spanned by one chain rather than 60 groups
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

        // split: a run of single-child levels down to the group where the stems part, each at its
        // shortest unique prefix
        root = harness.ApplyBatch([(keyB, valueB)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot([(keyA, valueA), (keyB, valueB)])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(splitGroupCount));
        AssertStoreMatchesFreshRebuild(harness, [(keyA, valueA), (keyB, valueB)]);

        // delete A: B hoists all the way back to the root group, tombstoning the whole run
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
        // the root group, the depth-4 group holding C beside the path on towards A and B, the run
        // spanning depths 8 to 20, and the depth-20 group where A and B part
        Assert.That(harness.Nodes, Has.Count.EqualTo(4));
        AssertStoreMatchesFreshRebuild(harness, [(keyA, valueA), (keyB, valueB), (keyC, valueC)]);

        // delete B: A hoists out through the whole run, which dissolves, and lands next to C at depth 7
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
    public void ChainAbsorbsAnUntouchedChildAndSplitsBackAroundASibling()
    {
        // A1 and A2 share their first 40 bits, so a run of single-child levels spans depths 8 to 40 —
        // eight groups' worth. B parts from them at bit 7, landing in the depth-4 group beside the slot
        // that run descends from.
        byte[] stemA1 = new byte[31];
        byte[] stemA2 = new byte[31];
        stemA2[5] = 0x80; // bit 40
        byte[] stemB = new byte[31];
        stemB[0] = 0x01; // bit 7
        byte[] keyA1 = [.. stemA1, 1];
        byte[] keyA2 = [.. stemA2, 2];
        byte[] keyB = [.. stemB, 3];
        byte[] valueA1 = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueA2 = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] valueB = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");
        (byte[], byte[]?)[] all = [(keyA1, valueA1), (keyA2, valueA2), (keyB, valueB)];

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        ValueHash256 root = harness.ApplyBatch(all);
        Assert.That(root, Is.EqualTo(ReferenceRoot(all)));
        // the root group, the depth-4 group holding B, the run from 8 to 40, and the group where A1/A2 part
        Assert.That(harness.Nodes, Has.Count.EqualTo(4));
        Assert.That(harness.Nodes.Keys, Has.Some.Matches<TrieNodeKey>(key => key.Depth == 8), "the run is stored where it starts");
        AssertStoreMatchesFreshRebuild(harness, all);

        // Delete B and the depth-4 group is left with one child this batch never descended into. Only
        // reading it reveals the run to merge with, and merging is what keeps a run as long as it can be.
        (byte[], byte[]?)[] justA = [(keyA1, valueA1), (keyA2, valueA2)];
        root = harness.ApplyBatch([(keyB, null)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot(justA)));
        Assert.That(harness.Nodes, Has.Count.EqualTo(3), "the run absorbed the one below it rather than pointing at it");
        AssertStoreMatchesFreshRebuild(harness, justA);

        // Re-inserting B splits the run inside its own top four levels. The remainder keeps the slot this
        // batch never touches, so nothing descends there — yet it must still be planted under the group
        // that replaces the run.
        root = harness.ApplyBatch([(keyB, valueB)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot(all)));
        Assert.That(harness.Nodes, Has.Count.EqualTo(4));
        AssertStoreMatchesFreshRebuild(harness, all);
    }

    [Test]
    public void ChainSplitDescendsALiveRemainderBucket()
    {
        // A1/A2 part at bit 40, so a run spans depths 4 to 40. C parts from them at bit 9 — inside the
        // depth-8 group, which the run reaches only by skipping the frame at depth 4.
        byte[] stemA1 = new byte[31];
        byte[] stemA2 = new byte[31];
        stemA2[5] = 0x80; // bit 40
        byte[] stemC = new byte[31];
        stemC[1] = 0x40; // bit 9
        byte[] keyA1 = [.. stemA1, 1];
        byte[] keyA2 = [.. stemA2, 2];
        byte[] keyC = [.. stemC, 3];
        byte[] valueA1 = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueA2 = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] valueC = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");
        byte[] rewritten = Bytes.FromHexString("0x4444444444444444444444444444444444444444444444444444444444444444");

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        ValueHash256 root = harness.ApplyBatch([(keyA1, valueA1), (keyA2, valueA2)]);
        Assert.That(harness.Nodes, Has.Count.EqualTo(3), "the root group, the run from 4 to 40, and the group at 40");

        // Rewriting A1 with the value it already holds folds to the same nodes throughout, so the run and
        // every group above it stay exactly as they were.
        Dictionary<TrieNodeKey, byte[]> before = new(harness.Nodes);
        Assert.That(harness.ApplyBatch([(keyA1, valueA1)]), Is.EqualTo(root), "a no-op write leaves the root alone");
        Assert.That(harness.Nodes, Is.EquivalentTo(before), "and rewrites nothing under the run");

        // C parts from the run below this frame's four levels, so the descent skips straight to the
        // depth-8 group holding the branch; A1's write shares the run's nibble there, so the remainder it
        // seeds is descended rather than merely passed through.
        (byte[], byte[]?)[] all = [(keyA1, rewritten), (keyA2, valueA2), (keyC, valueC)];
        root = harness.ApplyBatch([(keyA1, rewritten), (keyC, valueC)]);
        Assert.That(root, Is.EqualTo(ReferenceRoot(all)));
        // the root group, the run from 4 to 8, the group at 8 where C parts, the run from 12 to 40, and
        // the group at 40 — one run split into two by a branch landing between them
        Assert.That(harness.Nodes, Has.Count.EqualTo(5));
        AssertStoreMatchesFreshRebuild(harness, all);
    }

    [Test]
    public void ChainSpanningOneGroupSplitsAgainstItsTarget()
    {
        // A1/A2 part at bit 8, the shortest run there is: depth 4 to 8, one group's worth. D then parts at
        // bit 5, splitting it against a target that is already its direct child.
        byte[] stemA1 = new byte[31];
        byte[] stemA2 = new byte[31];
        stemA2[1] = 0x80; // bit 8
        byte[] stemD = new byte[31];
        stemD[0] = 0x04; // bit 5
        byte[] keyA1 = [.. stemA1, 1];
        byte[] keyA2 = [.. stemA2, 2];
        byte[] keyD = [.. stemD, 3];
        byte[] valueA1 = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueA2 = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] valueD = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        harness.ApplyBatch([(keyA1, valueA1), (keyA2, valueA2)]);
        Assert.That(harness.Nodes, Has.Count.EqualTo(3), "the root group, the run from 4 to 8, and the group at 8");

        (byte[], byte[]?)[] all = [(keyA1, valueA1), (keyA2, valueA2), (keyD, valueD)];
        Assert.That(harness.ApplyBatch([(keyD, valueD)]), Is.EqualTo(ReferenceRoot(all)));
        // the run is gone: its four levels are the depth-4 group now, pointing straight at what it targeted
        Assert.That(harness.Nodes, Has.Count.EqualTo(3));
        AssertStoreMatchesFreshRebuild(harness, all);
    }

    [Test]
    public void ContractStorageSharingItsStemPrefix_CollapsesToOneChain()
    {
        // The case the format is for: every storage stem of a contract is
        // 1 || blake3(address)[:60] || blake3(address || treeIndex)[:187], so they share 61 bits and the
        // trie has nothing but single-child levels from wherever the contract parts from others down to
        // the group at depth 60 — which is one run, not fifteen groups.
        Address contract = TestItem.AddressA;
        byte[] value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        List<(byte[], byte[]?)> writes = [];
        for (int slot = 0; slot < 3; slot++)
        {
            // past HeaderStorageOffset, and a whole tree index apart, so each takes a stem of its own
            writes.Add((PbtKeyDerivation.StorageKey(contract, (UInt256)(PbtKeyDerivation.HeaderStorageOffset + (slot << 8))).ToByteArray(), value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        Assert.That(harness.ApplyBatch(writes), Is.EqualTo(ReferenceRoot(writes)));
        AssertStoreMatchesFreshRebuild(harness, writes);

        // one run carries the whole shared prefix, and nothing is stored anywhere along it (invariant 3)
        TrieNodeKey[] chains = [.. harness.Nodes.Keys.Where(key => PbtNodeChain.IsChain(harness.Nodes[key]))];
        Assert.That(chains, Has.Length.EqualTo(1));

        PbtNodeChain chain = PbtNodeChain.Decode(harness.Nodes[chains[0]], chains[0].Depth);
        int levels = chain.TargetDepth - chains[0].Depth;
        for (int depth = chains[0].Depth + PbtTrieNodeGroup.LevelsPerGroup; depth < chain.TargetDepth; depth += PbtTrieNodeGroup.LevelsPerGroup)
        {
            Assert.That(harness.Nodes.ContainsKey(TrieNodeKey.For(depth, chain.TargetPath)), Is.False, $"nothing is stored at depth {depth}, inside the run");
        }

        // The prefix runs to bit 60, so every contract's storage produces this same shape: one run from
        // the root group down to the depth-60 group where the suffix starts branching. That is fourteen
        // groups — 14 * (9 + 5 * 32) = 2366 bytes of single-child levels — held in 97.
        Assert.That(chains[0].Depth, Is.EqualTo(PbtTrieNodeGroup.LevelsPerGroup));
        Assert.That(chain.TargetDepth, Is.EqualTo(60));
        Assert.That(levels / PbtTrieNodeGroup.LevelsPerGroup, Is.EqualTo(14));
        Assert.That(harness.Nodes[chains[0]], Has.Length.EqualTo(PbtNodeChain.EncodedLength));
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

    /// <summary>
    /// A range of at most three entries is partitioned by a sorting network rather than by counting into
    /// sixteen buckets, and a batch sharing one first byte diverges only past the two levels the producer
    /// buckets, so the network folds every level of these. Both the drained and the hand-built path share
    /// it there, which would cancel a bug out of a comparison between them — the reference tree is the
    /// independent oracle.
    /// </summary>
    [TestCaseSource(nameof(TinyRangeFixtures))]
    public void TinyBatchBelowTheProducersBuckets_FoldsToTheReferenceTree(byte[] secondBytes)
    {
        List<(byte[], byte[]?)> writes = [];
        for (int i = 0; i < secondBytes.Length; i++)
        {
            byte[] stem = new byte[Stem.Length];
            stem[0] = 0x80; // one shard, so the producer's own two levels never tell these apart
            stem[1] = secondBytes[i];
            stem[^1] = (byte)i; // parts stems sharing a second byte, far below the divergence under test
            byte[] value = new byte[32];
            value.AsSpan().Fill((byte)(i + 1));
            writes.Add(([.. stem, (byte)i], value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        Assert.That(harness.ApplyBatch(writes), Is.EqualTo(ReferenceRoot(writes)));
        AssertStoreMatchesFreshRebuild(harness, writes);
    }

    /// <summary>
    /// Second bytes whose depth-8 and depth-12 nibbles walk the bucket runs a sorted tiny range fills:
    /// the ends of the bounds array, and the empty runs entries sharing a nibble leave between them.
    /// </summary>
    private static IEnumerable<TestCaseData> TinyRangeFixtures()
    {
        yield return TinyFixture("one entry", [0x30]);
        yield return TinyFixture("two, ascending", [0x10, 0x20]);
        yield return TinyFixture("two, descending", [0x20, 0x10]);
        yield return TinyFixture("two sharing a nibble", [0x10, 0x10]);
        yield return TinyFixture("three, reversed", [0x30, 0x20, 0x10]);
        yield return TinyFixture("three, unsorted, two sharing a nibble", [0x20, 0x20, 0x10]);
        yield return TinyFixture("nibbles at either end", [0x00, 0xF0]);
        yield return TinyFixture("all three in the first nibble", [0x00, 0x00, 0x00]);
        yield return TinyFixture("all three in the last nibble", [0xF0, 0xF0, 0xF0]);
    }

    private static TestCaseData TinyFixture(string name, byte[] secondBytes) => new TestCaseData((object)secondBytes).SetArgDisplayNames(name);

    /// <summary>
    /// A rewrite in a group whose every position is present: the fifteen untouched boundary slots leave
    /// whole subtrees of it unchanged, which the fold copies verbatim out of the stored encoding rather
    /// than rebuilding node by node. The copy must be indistinguishable from that rebuild, which a
    /// from-scratch fold of the same stems is what pins.
    /// </summary>
    [Test]
    public void RewriteInADenseGroup_CopiesCleanSubtreesIdenticallyToAFreshFold()
    {
        byte[] value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] rewritten = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

        // one stem per boundary slot of the depth-4 group: they share the depth-0 nibble and differ in
        // the depth-4 one, so that group branches sixteen ways rather than collapsing or nesting
        List<(byte[] Key, byte[]? Value)> writes = [];
        for (byte slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++) writes.Add(([.. MakeStem(slot, 0), 5], value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance);
        harness.ApplyBatch(writes);

        PbtTrieNodeGroup dense = PbtTrieNodeGroup.Decode(harness.Nodes[TrieNodeKey.Root.ChildGroup(0)]);
        Assert.That(
            CountPresentPositions(dense), Is.EqualTo(PbtTrieNodeGroup.PositionCount),
            "the setup must really fill the group, or nothing here is clean enough to be copied");

        writes[3] = (writes[3].Key, rewritten);
        harness.ApplyBatch([writes[3]]);

        AssertStoreMatchesFreshRebuild(harness, writes);
    }

    private static int CountPresentPositions(PbtTrieNodeGroup group)
    {
        int present = 0;
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            if (group.KindAt(position) != PbtTrieNodeGroup.NodeKind.Absent) present++;
        }

        return present;
    }

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
    /// A rewrite that changes no value leaves every group above it exactly as stored, and none of them
    /// is folded to discover that. The stored bytes and the root are the same either way, so what the
    /// nesting must not move is the rents: one per group is what folding a fresh encoding costs.
    /// </summary>
    [Test]
    public void NoOpRewrite_FoldsNoGroupAboveTheStem()
    {
        (int shallowRents, int shallowGroups) = NoOpRewrite(deeplyNested: false);
        (int deepRents, int deepGroups) = NoOpRewrite(deeplyNested: true);

        Assert.That(deepGroups, Is.GreaterThan(shallowGroups), "the deep case must really nest the stem under more groups");
        Assert.That(deepRents, Is.EqualTo(shallowRents), "a group above the rewrite must not be folded only to find it unchanged");
    }

    /// <summary>
    /// Nests a stem under two groups — or three, when <paramref name="deeplyNested"/> — rewrites it with
    /// the value it already holds, and reports what that batch rented and how many groups are stored.
    /// </summary>
    private static (int Rents, int Groups) NoOpRewrite(bool deeplyNested)
    {
        byte[] value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] target = TwoByteStem(0x00, 0x00);

        // parts from the target at depth 4, so the group there branches rather than collapsing to a run
        List<(byte[], byte[]?)> writes = [([.. target, 5], value), ([.. TwoByteStem(0x01, 0x00), 5], value)];

        // and at depth 8, nesting the target one group deeper still
        if (deeplyNested) writes.Add(([.. TwoByteStem(0x00, 0x10), 5], value));

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider);
        harness.ApplyBatch(writes);

        int rentsBefore = provider.Rented.Count;
        harness.ApplyBatch([([.. target, 5], value)]);
        return (provider.Rented.Count - rentsBefore, harness.Nodes.Count);
    }

    /// <summary>A stem told apart from its fellows only by <paramref name="b0"/> and <paramref name="b1"/>, so it parts from them above depth 16.</summary>
    private static byte[] TwoByteStem(byte b0, byte b1)
    {
        byte[] stem = new byte[Stem.Length];
        stem[0] = b0;
        stem[1] = b1;
        return stem;
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
        stemB[20] = 0x01; // diverges deep, so the split leaves a run spanning many group boundaries
        byte[] stemC = new byte[31];
        stemC[0] = 0x01; // parts at bit 7, into the depth-4 group beside the slot the run descends from
        byte[] valueA = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
        byte[] valueB = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");
        byte[] valueC = Bytes.FromHexString("0x3333333333333333333333333333333333333333333333333333333333333333");

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider);

        // each batch settles nodes by a different path: fresh groups, an unchanged rebuild that is
        // dropped rather than written, a split that builds a run, a split of that run, a delete that
        // collapses a group onto an untouched child and absorbs the run below it, and one that dissolves
        // the run entirely and hoists the survivor back up
        harness.ApplyBatch([([.. stemA, 5], valueA)]);
        harness.ApplyBatch([([.. stemA, 5], valueA)]);
        harness.ApplyBatch([([.. stemB, 7], valueB)]);
        harness.ApplyBatch([([.. stemC, 9], valueC)]);
        harness.ApplyBatch([([.. stemC, 9], null)]);
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

        AssertChainsAreMaximal(harness);
        AssertStatsMatchSubtreeCounts(harness);
    }

    /// <summary>
    /// Every stored node's statistics count the stems its subtree actually holds
    /// (<see cref="PbtSubtreeStats"/>).
    /// </summary>
    /// <remarks>
    /// The rebuild comparison above already pins these, statistics being part of an encoding it
    /// compares byte for byte — but only as a byte that differs. Counting the stems says which node
    /// mis-hoisted, and how far out it is. The root's count is checked against the leaf blobs as well:
    /// a stem has exactly one, so the store says how many stems there are without walking the trie at
    /// all.
    /// </remarks>
    private static void AssertStatsMatchSubtreeCounts(PbtTreeHarness harness) =>
        Assert.That(CountStems(harness, TrieNodeKey.Root), Is.EqualTo(harness.Blobs.Count), "the root subtree is every stem");

    /// <summary>
    /// The stems under <paramref name="key"/>, counted from the store rather than hoisted, asserting
    /// on the way that the blob there says the same.
    /// </summary>
    private static long CountStems(PbtTreeHarness harness, in TrieNodeKey key)
    {
        if (!harness.Nodes.TryGetValue(key, out byte[]? blob)) return 0;

        if (PbtNodeChain.IsChain(blob))
        {
            PbtNodeChain chain = PbtNodeChain.Decode(blob, key.Depth);
            long reached = CountStems(harness, chain.TargetKey);
            Assert.That(chain.Stats.StemCount, Is.EqualTo(reached), $"run at {key}");
            return reached;
        }

        // A stem sits at whichever position is its shortest unique prefix, boundary or inner, so every
        // position is visited; only a boundary internal roots a blob of its own to descend into.
        PbtTrieNodeGroup group = PbtTrieNodeGroup.Decode(blob);
        long counted = 0;
        for (int position = 0; position < PbtTrieNodeGroup.PositionCount; position++)
        {
            switch (group.KindAt(position))
            {
                case PbtTrieNodeGroup.NodeKind.Stem:
                    counted++;
                    break;
                case PbtTrieNodeGroup.NodeKind.Internal when PbtTrieNodeGroup.IsBoundaryPosition(position):
                    counted += CountStems(harness, key.ChildGroup(PbtTrieNodeGroup.BoundarySlot(position)));
                    break;
            }
        }

        Assert.That(group.Stats.StemCount, Is.EqualTo(counted), $"group at {key}");
        return counted;
    }

    /// <summary>
    /// Every run reaches a stored group, and holds every level down to it (<see cref="PbtNodeChain"/>
    /// invariants 2 and 3).
    /// </summary>
    /// <remarks>
    /// A rebuild touches every node it makes, so it never meets the one case that can leave a run short —
    /// a group collapsing onto a child it never descended into — which is what lets the comparison above
    /// stand in for canonicality at all. Checking the runs directly says so where they break, rather than
    /// as a byte that differs.
    /// </remarks>
    private static void AssertChainsAreMaximal(PbtTreeHarness harness)
    {
        foreach ((TrieNodeKey key, byte[] blob) in harness.Nodes)
        {
            if (!PbtNodeChain.IsChain(blob)) continue;

            PbtNodeChain chain = PbtNodeChain.Decode(blob, key.Depth);
            for (int depth = key.Depth + PbtTrieNodeGroup.LevelsPerGroup; depth < chain.TargetDepth; depth += PbtTrieNodeGroup.LevelsPerGroup)
            {
                Assert.That(harness.Nodes.ContainsKey(TrieNodeKey.For(depth, chain.TargetPath)), Is.False, $"{key} spans depth {depth}, which must hold nothing");
            }

            Assert.That(harness.Nodes.TryGetValue(chain.TargetKey, out byte[]? target), $"{key} targets {chain.TargetKey}, which holds nothing");
            Assert.That(PbtNodeChain.IsChain(target), Is.False, $"{key} targets another run at {chain.TargetKey} rather than absorbing it");
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
