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

using Layout = Nethermind.Pbt.Tiles.PbtFourLevelTileLayout;
using Nethermind.Pbt.Tiles;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Verifies that layouts of the same tiling interoperate without a migration.
/// </summary>
public class PbtFormatInteropTests
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] Rewritten = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

    /// <remarks>Formats are ordered by increasing level skipping, so stored byte totals must decrease.</remarks>
    [Test]
    public void EveryFormat_FoldsToTheSameRoot_AndSkippingMoreStoresFewerBytes()
    {
        List<(byte[], byte[]?)> writes = RandomWrites(seed: 7, count: 400);

        // Every4Depth shares BoundaryOnly's tile bytes; its interop is covered below.
        PbtTreeHarness[] harnesses =
        [
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.FourLevelEveryLevel),
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.FourLevelInterleaved),
            new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.FourLevelBoundaryOnly),
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

    /// <summary>Verifies that rewriting across layouts matches a fresh fold in the target layout.</summary>
    /// <remarks>The eight-level pair covers <see cref="PbtGroupFormat.Every4Depth"/>.</remarks>
    [TestCase(PbtTrieLayout.FourLevelEveryLevel, PbtTrieLayout.FourLevelInterleaved)]
    [TestCase(PbtTrieLayout.FourLevelInterleaved, PbtTrieLayout.FourLevelEveryLevel)]
    [TestCase(PbtTrieLayout.FourLevelEveryLevel, PbtTrieLayout.FourLevelBoundaryOnly)]
    [TestCase(PbtTrieLayout.FourLevelBoundaryOnly, PbtTrieLayout.FourLevelEveryLevel)]
    [TestCase(PbtTrieLayout.FourLevelInterleaved, PbtTrieLayout.FourLevelBoundaryOnly)]
    [TestCase(PbtTrieLayout.FourLevelBoundaryOnly, PbtTrieLayout.FourLevelInterleaved)]
    [TestCase(PbtTrieLayout.EightLevelInterleaved, PbtTrieLayout.EightLevelEvery4Depth)]
    [TestCase(PbtTrieLayout.EightLevelEvery4Depth, PbtTrieLayout.EightLevelInterleaved)]
    [TestCase(PbtTrieLayout.SixLevelInterleaved, PbtTrieLayout.SixLevelEvery3Depth)]
    [TestCase(PbtTrieLayout.SixLevelEvery3Depth, PbtTrieLayout.SixLevelInterleaved)]
    public void MixedLayoutRewrite_MatchesAFreshFoldInTheNewLayout(PbtTrieLayout initial, PbtTrieLayout then)
    {
        // A full tile leaves unchanged subtrees for the copy-verbatim path after one-slot rewrites.
        int levelsPerGroup = initial.Tiling() switch
        {
            PbtTiling.SixLevel => PbtSixLevelTileLayout.LevelsPerGroup,
            PbtTiling.EightLevel => PbtEightLevelTileLayout.LevelsPerGroup,
            _ => Layout.LevelsPerGroup,
        };
        int slots = 1 << levelsPerGroup;
        List<(byte[], byte[]?)> writes = [];
        for (int slot = 0; slot < slots; slot++) writes.Add((RootTileSlotKey(slot, levelsPerGroup), Value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, initial);
        harness.ApplyBatch(writes);

        harness.WriteLayout = then;
        writes[3] = (writes[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([writes[3]]);

        // Fold the surviving state entirely in the target layout.
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
    public void SixLevelEvery3Depth_WritesEvery3GroupsAndInterleavedLeaves()
    {
        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.SixLevelEvery3Depth);
        harness.ApplyBatch(RandomWrites(seed: 19, count: 80));

        Assert.That(harness.Nodes, Is.Not.Empty);
        foreach (byte[] group in harness.Nodes.Values)
        {
            Assert.That(group[^1], Is.EqualTo((byte)PbtGroupFormat.Every3Depth), "trie group format");
        }

        Assert.That(harness.Blobs, Is.Not.Empty);
        foreach (byte[] leaf in harness.Blobs.Values)
        {
            Assert.That(leaf[^1], Is.EqualTo((byte)PbtLeafFormat.Interleaved), "leaf blob format");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That((byte)PbtGroupFormat.Every3Depth, Is.EqualTo(0x09));
            Assert.That((byte)PbtLeafFormat.Interleaved, Is.EqualTo(0x03));
        }
    }

    [Test]
    public void OldFormatStore_ReadsAndConvertsOnlyWhatIsRewritten()
    {
        // Two disjoint dense root children allow rewriting one while preserving the other.
        List<(byte[], byte[]?)> groupA = [];
        List<(byte[], byte[]?)> groupB = [];
        for (byte slot = 0; slot < Layout.BoundarySlots; slot++)
        {
            groupA.Add((BoundaryKey(0, slot), Value));
            groupB.Add((BoundaryKey(1, slot), Value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTrieLayout.FourLevelEveryLevel);
        harness.ApplyBatch([.. groupA, .. groupB]);
        TrieNodeKey keyA = PbtPartitions.RootKey(PbtPartition.Account).ChildGroup(0, Layout.LevelsPerGroup);
        TrieNodeKey keyB = PbtPartitions.RootKey(PbtPartition.Account).ChildGroup(1, Layout.LevelsPerGroup);
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel));

        harness.WriteLayout = PbtTrieLayout.FourLevelInterleaved;
        groupA[3] = (groupA[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([groupA[3]]);

        Assert.That(root, Is.EqualTo(ReferenceRoot([.. groupA, .. groupB])), "the old-format store still reads correctly");
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.Interleaved), "a rewritten group converts");
        Assert.That(PbtTrieNodeGroup<Layout>.Decode(harness.Nodes[keyB]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel), "an untouched one is left as it was");
    }

    private static long TotalNodeBytes(PbtTreeHarness harness) => harness.Nodes.Values.Sum(blob => (long)blob.Length);

    private static byte[] BoundaryKey(byte rootNibble, byte slot) =>
        TileSlotKey((byte)((rootNibble << 4) | slot)); // Root and depth-4 nibbles select the group and slot.

    /// <summary>Creates a key for one account partition-root tile boundary slot.</summary>
    private static byte[] RootTileSlotKey(int slot, int levelsPerGroup)
    {
        int prefix = slot << (12 - levelsPerGroup);
        byte[] key = new byte[Stem.Length + 1];
        key[0] = (byte)(prefix >> 8);
        key[1] = (byte)prefix;
        return key;
    }

    /// <summary>Creates a key whose first byte identifies a boundary slot.</summary>
    private static byte[] TileSlotKey(byte path)
    {
        byte[] key = new byte[Stem.Length + 1];
        key[0] = (byte)(path >> 4);
        key[1] = (byte)(path << 4);
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
            key[0] &= 0x0F;
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
