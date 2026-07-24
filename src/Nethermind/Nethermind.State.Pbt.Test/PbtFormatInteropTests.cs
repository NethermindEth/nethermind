// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

using Layout = Nethermind.Pbt.PbtClusteredTileLayout;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// The group encodings all describe the same trie, so they interoperate: any of them reads any other,
/// they fold to the same root, and a store written across a format switch stays correct — the
/// guarantee that lets <c>Pbt.TrieNodeLayout</c> move between layouts of one tiling without a migration.
/// </summary>
public class PbtFormatInteropTests
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] Rewritten = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

    /// <remarks>
    /// The formats are listed in the order they skip more, which is what the byte totals must come out
    /// in: each stores a subset of the levels the one before it does.
    /// </remarks>
    [Test]
    public void EveryFormat_FoldsToTheSameRoot_AndSkippingMoreStoresFewerBytes()
    {
        List<(byte[], byte[]?)> writes = RandomWrites(seed: 7, count: 400);

        // Every4Depth is left out: it stores the same tile as BoundaryOnly and differs only in the leaf
        // column, which TotalNodeBytes does not weigh, so it cannot be ordered here. Its fold and node set
        // are covered by MixedLayoutRewrite below and its leaf column by StemLeafBlobTests.
        PbtTreeHarness[] harnesses =
        [
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.ClusteredFourLevelEveryLevel),
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.ClusteredFourLevelInterleaved),
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.ClusteredFourLevelBoundaryOnly),
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach (PbtTreeHarness harness in harnesses)
            {
                Assert.That(harness.ApplyBatch(writes), Is.EqualTo(ReferenceRoot(writes)), $"{harness.WriteLayout} folds to the EIP-8297 reference root");
                Assert.That(harness.Nodes.Keys, Is.EquivalentTo(harnesses[0].Nodes.Keys), $"{harness.WriteLayout} changes bytes, not the node set");
            }

            for (int i = 1; i < harnesses.Length; i++)
            {
                Assert.That(
                    TotalNodeBytes(harnesses[i]), Is.LessThan(TotalNodeBytes(harnesses[i - 1])),
                    $"{harnesses[i].WriteLayout} stores fewer bytes than {harnesses[i - 1].WriteLayout}");
            }
        }
    }

    /// <summary>
    /// A group first written in one layout and then rewritten in another of the same tiling must come
    /// out byte-identical to one folded in that layout from scratch — the copy-verbatim path must refold
    /// across the format change rather than splice old bytes into the new encoding.
    /// </summary>
    /// <remarks>
    /// The eight-level pair is where <see cref="PbtGroupFormat.Every4Depth"/> is covered, no clustered
    /// layout writing it.
    /// </remarks>
    [TestCase(PbtTrieLayout.ClusteredFourLevelEveryLevel, PbtTrieLayout.ClusteredFourLevelInterleaved)]
    [TestCase(PbtTrieLayout.ClusteredFourLevelInterleaved, PbtTrieLayout.ClusteredFourLevelEveryLevel)]
    [TestCase(PbtTrieLayout.ClusteredFourLevelEveryLevel, PbtTrieLayout.ClusteredFourLevelBoundaryOnly)]
    [TestCase(PbtTrieLayout.ClusteredFourLevelBoundaryOnly, PbtTrieLayout.ClusteredFourLevelEveryLevel)]
    [TestCase(PbtTrieLayout.ClusteredFourLevelInterleaved, PbtTrieLayout.ClusteredFourLevelBoundaryOnly)]
    [TestCase(PbtTrieLayout.ClusteredFourLevelBoundaryOnly, PbtTrieLayout.ClusteredFourLevelInterleaved)]
    [TestCase(PbtTrieLayout.EightLevelInterleaved, PbtTrieLayout.EightLevelEvery4Depth, Ignore = PbtTestFormats.EightLevelFoldUnfinished)]
    [TestCase(PbtTrieLayout.EightLevelEvery4Depth, PbtTrieLayout.EightLevelInterleaved, Ignore = PbtTestFormats.EightLevelFoldUnfinished)]
    public void MixedLayoutRewrite_MatchesAFreshFoldInTheNewLayout(PbtTrieLayout initial, PbtTrieLayout then)
    {
        // one tile filled at every boundary slot: it branches every way it can, so a single-slot rewrite
        // leaves whole clean subtrees for the copy-verbatim path to take
        int slots = initial.Tiling() == PbtTiling.EightLevel ? PbtEightLevelTileLayout.BoundarySlots : Layout.BoundarySlots;
        List<(byte[], byte[]?)> writes = [];
        for (int slot = 0; slot < slots; slot++) writes.Add((TileSlotKey((byte)slot), Value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, initial);
        harness.ApplyBatch(writes);

        harness.WriteLayout = then;
        writes[3] = (writes[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([writes[3]]);

        // a fresh fold of the surviving state entirely in the new layout
        PbtTreeHarness fresh = new(PooledRefCountingMemoryProvider.Instance, then);
        ValueHash256 freshRoot = fresh.ApplyBatch(writes);

        Assert.That(root, Is.EqualTo(freshRoot), "the rewrite must reach the same root");
        Assert.That(root, Is.EqualTo(ReferenceRoot(writes)));
        Assert.That(harness.Nodes.Keys, Is.EquivalentTo(fresh.Nodes.Keys), "same node set");
        foreach ((TrieNodeKey key, byte[] blob) in fresh.Nodes)
        {
            Assert.That(harness.Nodes[key], Is.EqualTo(blob), $"node {key} must match a fresh {then} fold, not splice {initial} bytes");
        }
    }

    [Test]
    public void OldFormatStore_ReadsAndConvertsOnlyWhatIsRewritten()
    {
        // two disjoint dense groups under the root, so a later write can touch one and leave the other
        List<(byte[], byte[]?)> groupA = [];
        List<(byte[], byte[]?)> groupB = [];
        for (byte slot = 0; slot < Layout.BoundarySlots; slot++)
        {
            groupA.Add((BoundaryKey(0, slot), Value));
            groupB.Add((BoundaryKey(1, slot), Value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.ClusteredFourLevelEveryLevel);
        harness.ApplyBatch([.. groupA, .. groupB]);
        TrieNodeKey keyA = TrieNodeKey.Root.ChildGroup(0, Layout.LevelsPerGroup);
        TrieNodeKey keyB = TrieNodeKey.Root.ChildGroup(1, Layout.LevelsPerGroup);
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel));

        harness.WriteLayout = PbtTrieLayout.ClusteredFourLevelInterleaved;
        groupA[3] = (groupA[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([groupA[3]]);

        Assert.That(root, Is.EqualTo(ReferenceRoot([.. groupA, .. groupB])), "the old-format store still reads correctly");
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.Interleaved), "a rewritten group converts");
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyB]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel), "an untouched one is left as it was");
    }

    private static long TotalNodeBytes(PbtTreeHarness harness) => harness.Nodes.Values.Sum(blob => (long)blob.Length);

    private static byte[] BoundaryKey(byte rootNibble, byte slot) =>
        TileSlotKey((byte)((rootNibble << 4) | slot)); // depth-0 nibble picks the child group, depth-4 nibble the slot

    /// <summary>
    /// A key whose first byte is its whole path down to a boundary slot: of the depth-4 group under a
    /// root nibble on the four-level tiling, of the root tile itself on the eight-level one.
    /// </summary>
    private static byte[] TileSlotKey(byte path)
    {
        byte[] key = new byte[Stem.Length + 1];
        key[0] = path;
        return key;
    }

    private static List<(byte[], byte[]?)> RandomWrites(int seed, int count)
    {
        Random random = new(seed);
        List<(byte[], byte[]?)> writes = [];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[Stem.Length + 1];
            random.NextBytes(key);
            byte[] value = new byte[32];
            random.NextBytes(value);
            writes.Add((key, value));
        }

        return writes;
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
