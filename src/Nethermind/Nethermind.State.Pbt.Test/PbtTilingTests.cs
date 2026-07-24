// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// What every tiling of the stem trie must do, whichever it is: fold to the EIP-8297 root, keep the
/// canonical store shape a fresh rebuild would produce, and key nothing past its deepest tile.
/// </summary>
/// <remarks>
/// <see cref="StemTrieTests"/> covers the four-level tiling in structural detail — the depths its
/// groups sit at, the counts its runs collapse to — none of which carries over to a tiling of another
/// width. What does carry over is here, run under every layout, with <see cref="EipReferenceTree"/> as
/// the oracle throughout: a tiling changes where the bytes live and nothing else.
/// </remarks>
[TestFixture(PbtTrieLayout.ClusteredFourLevelEveryLevel)]
[TestFixture(PbtTrieLayout.ClusteredFourLevelInterleaved)]
[TestFixture(PbtTrieLayout.ClusteredFourLevelBoundaryOnly)]
[TestFixture(PbtTrieLayout.SixLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelInterleaved, Ignore = PbtTestFormats.EightLevelFoldUnfinished)]
[TestFixture(PbtTrieLayout.EightLevelEvery4Depth, Ignore = PbtTestFormats.EightLevelFoldUnfinished)]
public class PbtTilingTests(PbtTrieLayout layout)
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] Rewritten = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

    private int LevelsPerGroup => layout.Tiling() switch
    {
        PbtTiling.SixLevel => PbtSixLevelTileLayout.LevelsPerGroup,
        PbtTiling.EightLevel => PbtEightLevelTileLayout.LevelsPerGroup,
        _ => PbtClusteredTileLayout.LevelsPerGroup,
    };

    private int MaxGroupDepth => layout.Tiling() switch
    {
        PbtTiling.SixLevel => PbtSixLevelTileLayout.MaxGroupDepth,
        PbtTiling.EightLevel => PbtEightLevelTileLayout.MaxGroupDepth,
        _ => PbtClusteredTileLayout.MaxGroupDepth,
    };

    /// <summary>The depth of the tile holding trie level <paramref name="bit"/>, which is where a key for it sits.</summary>
    private int GroupDepthOf(int bit) => bit - bit % LevelsPerGroup;

    private PbtTreeHarness NewHarness() => new(PooledRefCountingMemoryProvider.Instance, layout);

    /// <summary>
    /// The stems part at <paramref name="divergenceBit"/>, so the store holds the root group, the group
    /// they part in, and — once those are not the same group and not adjacent — one run spanning
    /// everything between. Deleting either hoists the survivor back to a lone stem in the root group.
    /// </summary>
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(10)]
    [TestCase(163)]
    [TestCase(164)]
    [TestCase(245)]  // inside the deepest tile of either tiling
    [TestCase(247)]  // the last stem bit: the six-level tiling's deepest tile reaches past it
    public void StemsPartingAtAnyBit_FoldToTheReferenceRootAndCollapseBack(int divergenceBit)
    {
        byte[] stemA = new byte[Stem.Length];
        byte[] stemB = new byte[Stem.Length];
        stemB[divergenceBit >> 3] = (byte)(1 << (7 - (divergenceBit & 7)));
        (byte[] Key, byte[]? Value) a = ([.. stemA, (byte)5], Value);
        (byte[] Key, byte[]? Value) b = ([.. stemB, (byte)7], Rewritten);

        PbtTreeHarness harness = NewHarness();
        Assert.That(harness.ApplyBatch([a]), Is.EqualTo(ReferenceRoot([a])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1), "a lone stem is the root group and nothing else");

        Assert.That(harness.ApplyBatch([b]), Is.EqualTo(ReferenceRoot([a, b])));
        AssertCanonical(harness, [a, b]);

        // the group the stems part in is the deepest key the store holds, and its depth is a tile boundary
        int branchDepth = GroupDepthOf(divergenceBit);
        Assert.That(harness.Nodes.Keys.Max(key => key.Depth), Is.EqualTo(branchDepth));
        Assert.That(branchDepth, Is.LessThanOrEqualTo(MaxGroupDepth), "no key sits past the deepest tile");

        // deleting one hoists the other back into the root group, taking every group below it away
        Assert.That(harness.ApplyBatch([(b.Key, null)]), Is.EqualTo(ReferenceRoot([a])));
        Assert.That(harness.Nodes, Has.Count.EqualTo(1));
        Assert.That(harness.ApplyBatch([(a.Key, null)]), Is.EqualTo(default(ValueHash256)));
        Assert.That(harness.Nodes, Is.Empty);
    }

    /// <summary>
    /// Two stems agreeing on every bit but the last part inside the deepest tile of the tiling — the one
    /// that reaches past the 248-bit stem where six levels do not divide it. Each lands alone in a slot
    /// of its own, so the fold hoists both to their shortest unique prefix and no node is built below
    /// the stem level, which is what leaves the root the reference's.
    /// </summary>
    [Test]
    public void StemsPartingAtTheLastBit_BuildNoNodeBelowTheStemLevel()
    {
        byte[] stemA = new byte[Stem.Length];
        byte[] stemB = new byte[Stem.Length];
        stemB[^1] = 1;
        (byte[] Key, byte[]? Value) a = ([.. stemA, (byte)5], Value);
        (byte[] Key, byte[]? Value) b = ([.. stemB, (byte)7], Rewritten);

        PbtTreeHarness harness = NewHarness();
        Assert.That(harness.ApplyBatch([a, b]), Is.EqualTo(ReferenceRoot([a, b])));
        AssertCanonical(harness, [a, b]);
        Assert.That(harness.Nodes.Keys.Max(key => key.Depth), Is.EqualTo(MaxGroupDepth));

        // and the same stem twice in one batch is still the batch error it is at any other depth
        using PbtWriteBatch duplicate = new(estimatedStems: 2, buckets: null);
        ValueHash256 leaf = new(Value);
        duplicate.Add(new Stem(stemA), PbtStemChanges.Rent().Set(5, leaf));
        duplicate.Add(new Stem(stemA), PbtStemChanges.Rent().Set(7, leaf));
        Assert.That(
            () => TrieUpdater.UpdateRoot(NewHarness(), default, duplicate, PooledRefCountingMemoryProvider.Instance, layout, concurrency: 1, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    /// <summary>
    /// One contract's storage stems share their first 61 bits, so the trie is single-child from wherever
    /// the contract parts from others down to the group holding bit 60 — one run whatever the tiling,
    /// only its target depth moving with the tile width.
    /// </summary>
    [Test]
    public void ContractStorageSharingItsStemPrefix_CollapsesToOneChain()
    {
        List<(byte[] Key, byte[]? Value)> writes = [];
        for (int slot = 0; slot < 3; slot++)
        {
            writes.Add((PbtKeyDerivation.StorageKey(TestItem.AddressA, (UInt256)(PbtKeyDerivation.HeaderStorageOffset + (slot << 8))).ToByteArray(), Value));
        }

        PbtTreeHarness harness = NewHarness();
        Assert.That(harness.ApplyBatch(writes), Is.EqualTo(ReferenceRoot(writes)));
        AssertCanonical(harness, writes);

        Dictionary<TrieNodeKey, byte[]> nodes = harness.FlattenedNodes();
        TrieNodeKey[] chains = [.. nodes.Keys.Where(key => PbtNodeChain.IsChain(nodes[key]))];
        Assert.That(chains, Has.Length.EqualTo(1), "the whole shared prefix is one run");
        Assert.That(chains[0].Depth, Is.EqualTo(LevelsPerGroup), "which starts one tile below the root");

        // nothing is keyed anywhere along it (invariant 3), and its target is the tile holding bit 60
        for (int depth = chains[0].Depth; depth < GroupDepthOf(60); depth += LevelsPerGroup)
        {
            Assert.That(harness.Nodes.ContainsKey(TrieNodeKey.For(depth, new Stem(writes[0].Key.AsSpan(0, Stem.Length)))), Is.False, $"nothing is keyed at depth {depth}");
        }

        Assert.That(harness.Nodes.Keys.Max(key => key.Depth), Is.EqualTo(GroupDepthOf(60)));
    }

    /// <summary>
    /// A tile full at its boundary: one stem per slot, so the tile branches every way it can and a
    /// single-slot rewrite leaves whole clean subtrees for the fold to copy verbatim rather than
    /// rebuild. The copy must be indistinguishable from the rebuild, which a fresh fold pins.
    /// </summary>
    [Test]
    public void RewriteInADenseTile_CopiesCleanSubtreesIdenticallyToAFreshFold()
    {
        int slots = 1 << LevelsPerGroup;
        List<(byte[] Key, byte[]? Value)> writes = [];
        for (int slot = 0; slot < slots; slot++) writes.Add((BoundaryKey(slot), Value));

        PbtTreeHarness harness = NewHarness();
        harness.ApplyBatch(writes);
        Assert.That(harness.Blobs, Has.Count.EqualTo(slots), "the setup must really fill the tile");

        writes[3] = (writes[3].Key, Rewritten);
        Assert.That(harness.ApplyBatch([writes[3]]), Is.EqualTo(ReferenceRoot(writes)));
        AssertCanonical(harness, writes);
    }

    /// <summary>
    /// Rounds of pseudo-random inserts, updates and deletes over a store that carries across them, with
    /// the root checked against the oracle every round and the bytes against a fresh fold of the
    /// surviving state — which is what pins that an incremental rebuild leaves the canonical form.
    /// </summary>
    [TestCase(false, TestName = "RandomRounds_UnbucketedBatches")]
    [TestCase(true, TestName = "RandomRounds_BatchesDrainedThroughTheProducer")]
    public void RandomRounds_MatchTheOracleAndAFreshFold(bool drained)
    {
        Random random = new(Seed: 42);
        PbtTreeHarness harness = NewHarness();
        Dictionary<string, byte[]> live = [];

        for (int round = 0; round < 12; round++)
        {
            Dictionary<string, (byte[] Key, byte[]? Value)> batch = [];
            for (int i = 0; i < 20; i++)
            {
                byte[] key = RandomKey(random, live);
                byte[]? value = random.Next(4) == 0 ? null : RandomValue(random);
                batch[key.ToHexString()] = (key, value);
            }

            foreach ((string hex, (byte[] key, byte[]? value)) in batch)
            {
                if (value is null) live.Remove(hex);
                else live[hex] = value;
            }

            List<(byte[] Key, byte[]? Value)> writes = [.. batch.Values];
            ValueHash256 root = drained ? harness.ApplyDrainedBatch(writes) : harness.ApplyBatch(writes);
            Assert.That(root, Is.EqualTo(ReferenceRoot(LiveEntries(live))), $"root mismatch after round {round} under {layout}");
        }

        AssertCanonical(harness, LiveEntries(live));
    }

    /// <summary>
    /// The tilings describe the same trie, so the same writes fold to the same root under any of them —
    /// which is the whole claim a second tiling rests on.
    /// </summary>
    [Test]
    public void EveryTiling_FoldsTheSameWritesToTheSameRoot()
    {
        Random random = new(Seed: 7);
        Dictionary<string, byte[]> live = [];
        List<(byte[] Key, byte[]? Value)> writes = [];
        for (int i = 0; i < 200; i++)
        {
            byte[] key = RandomKey(random, live);
            live[key.ToHexString()] = Value;
            writes.Add((key, RandomValue(random)));
        }

        // storage stems as well, whose shared prefixes are what put runs in the store
        for (int slot = 0; slot < 8; slot++)
        {
            writes.Add((PbtKeyDerivation.StorageKey(TestItem.AddressB, (UInt256)(PbtKeyDerivation.HeaderStorageOffset + (slot << 8))).ToByteArray(), Value));
        }

        PbtTrieLayout otherLayout = layout.Tiling() == PbtTiling.ClusteredFourLevel
            ? PbtTrieLayout.SixLevelInterleaved
            : PbtTrieLayout.ClusteredFourLevelInterleaved;
        PbtTreeHarness other = new(PooledRefCountingMemoryProvider.Instance, otherLayout);
        Assert.That(NewHarness().ApplyBatch(writes), Is.EqualTo(other.ApplyBatch(writes)));
        Assert.That(other.ApplyBatch(writes), Is.EqualTo(ReferenceRoot(writes)));
    }

    /// <summary>A key whose path leads into boundary <paramref name="slot"/> of the first tile below the root.</summary>
    private byte[] BoundaryKey(int slot) =>
        [.. TrieNodeKey.Root.ChildGroup(0, LevelsPerGroup).ChildGroup(slot, LevelsPerGroup).Path.Bytes, (byte)5];

    /// <summary>Either a fresh random key, or one already live, so that updates and deletes hit something.</summary>
    private static byte[] RandomKey(Random random, Dictionary<string, byte[]> live)
    {
        if (live.Count != 0 && random.Next(3) == 0)
        {
            string hex = live.Keys.ElementAt(random.Next(live.Count));
            return Bytes.FromHexString(hex);
        }

        byte[] key = new byte[32];
        random.NextBytes(key);

        // an account-zone stem, so that a good share of the keys share prefixes rather than spreading
        // over the whole space
        key[0] = (byte)(random.Next(2) == 0 ? 0x00 : key[0] & 0x0F);
        return key;
    }

    private static byte[] RandomValue(Random random)
    {
        byte[] value = new byte[32];
        random.NextBytes(value);
        return value;
    }

    private static IEnumerable<(byte[] Key, byte[]? Value)> LiveEntries(Dictionary<string, byte[]> live) =>
        live.Select(entry => (Bytes.FromHexString(entry.Key), (byte[]?)entry.Value));

    /// <summary>
    /// Checks the store is byte-for-byte what a fresh fold of <paramref name="live"/> produces: an
    /// incremental rebuild must leave no trace of what it replaced.
    /// </summary>
    private void AssertCanonical(PbtTreeHarness harness, IEnumerable<(byte[] Key, byte[]? Value)> live)
    {
        PbtTreeHarness fresh = NewHarness();
        fresh.ApplyBatch([.. live.Where(entry => entry.Value is not null)]);

        Assert.That(harness.Nodes.Keys, Is.EquivalentTo(fresh.Nodes.Keys), "node set differs from a fresh fold");
        foreach ((TrieNodeKey key, byte[] node) in fresh.Nodes)
        {
            Assert.That(harness.Nodes[key], Is.EqualTo(node), $"node {key} differs from a fresh fold");
        }

        Assert.That(harness.Blobs.Keys, Is.EquivalentTo(fresh.Blobs.Keys), "leaf blob set differs from a fresh fold");
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
