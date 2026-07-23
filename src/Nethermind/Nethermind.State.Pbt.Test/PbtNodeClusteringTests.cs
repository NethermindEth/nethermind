// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

using Layout = Nethermind.Pbt.PbtClusteredTileLayout;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Where the updater puts each blob, as against what the trie holds: a group at a
/// <see cref="Layout.IsClusteringDepth"/> depth holds its children's blobs, so their keys hold
/// nothing, and a node moving across that boundary has to move its bytes with it.
/// </summary>
/// <remarks>
/// The roots and the canonical shape are <see cref="StemTrieTests"/>'s business, and every scenario
/// here goes through <c>AssertStoreMatchesFreshRebuild</c> there too by construction; what these pin
/// is the placement alone, which a byte-identical store would agree on however wrong it was.
/// </remarks>
public class PbtNodeClusteringTests
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] Other = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

    /// <summary>
    /// Every other group level is reached without a lookup of its own. A spine of stems parting one
    /// nibble deeper each time puts a branching group at every group depth, and half of them — the
    /// odd ones, depth 0 not clustering — have no key at all.
    /// </summary>
    [Test]
    public void EveryOtherGroupLevelIsHeldByTheOneAboveIt()
    {
        // stem i is zero but for nibble i, so it parts from every deeper one inside the group at 4i
        const int Levels = 8;
        List<(byte[], byte[]?)> writes = [];
        for (int level = 0; level < Levels; level++)
        {
            byte[] stem = new byte[Stem.Length];
            stem[level >> 1] = (byte)((level & 1) == 0 ? 0x10 : 0x01);
            writes.Add(([.. stem, 5], Value));
        }

        // the all-zero stem, which stays on the spine past every one of them
        writes.Add(([.. new byte[Stem.Length], 5], Value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTestFormats.Clustered(PbtGroupFormat.Interleaved));
        harness.ApplyBatch(writes);

        int[] nodeDepths = [.. harness.FlattenedNodes().Keys.Select(key => (int)key.Depth).Order()];
        Assert.That(nodeDepths, Is.EqualTo(Enumerable.Range(0, Levels).Select(level => level * Layout.LevelsPerGroup)));
        Assert.That(
            harness.Nodes.Keys.Select(key => (int)key.Depth).Order(),
            Is.EqualTo(nodeDepths.Where(depth => depth == 0 || !Layout.IsClusteringDepth(depth - Layout.LevelsPerGroup))),
            "a group whose parent clusters has no key of its own; the root has no parent");

        // and the bytes at the key that does hold it are the child's own encoding, verbatim
        TrieNodeKey clusteredChildKey = new TrieNodeKey((byte)Layout.LevelsPerGroup, default).ChildGroup(0, Layout.LevelsPerGroup);
        byte[] blob = harness.Nodes[new TrieNodeKey((byte)Layout.LevelsPerGroup, default)];
        PbtNodeCluster cluster = PbtNodeCluster.Decode<Layout>(blob, out PbtTrieNodeGroup<Layout> group);
        Assert.That(blob[cluster.Child(0, group)], Is.EqualTo(harness.FlattenedNodes()[clusteredChildKey]));
    }

    /// <summary>
    /// A run lands wherever the trie next branches, so its target's depth decides for itself whether
    /// that group holds its own children — which is the whole reason the parity is the depth's rather
    /// than the descent's.
    /// </summary>
    [TestCase(20, false, TestName = "ChainTargetAtAClusteringDepth_HoldsItsChild")]
    [TestCase(24, true, TestName = "ChainTargetBelowOne_LeavesItsChildAKey")]
    public void ChainTarget_ClustersByItsOwnDepth(int targetDepth, bool childHasKey)
    {
        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTestFormats.Clustered(PbtGroupFormat.Interleaved));
        harness.ApplyBatch(BranchingUnder(targetDepth));

        TrieNodeKey target = TrieNodeKey.For(targetDepth, default);
        Assert.That(harness.Nodes.Keys, Does.Contain(target), "a run's target always keeps a key of its own");
        Assert.That(harness.Nodes.ContainsKey(target.ChildGroup(0, Layout.LevelsPerGroup)), Is.EqualTo(childHasKey));
        Assert.That(harness.FlattenedNodes().Keys, Does.Contain(target.ChildGroup(0, Layout.LevelsPerGroup)), "either way the trie holds it");
    }

    /// <summary>
    /// A run splitting onto its own target makes a group where there was none, and where that group
    /// clusters, the target must move out of the key the run left it under and into the new blob — then
    /// back out again when deleting the stem collapses the group back into the run.
    /// </summary>
    [Test]
    public void ARunSplittingOntoItsTarget_AdoptsItAndGivesItBack()
    {
        // the run reaches depth 24, so a stem parting at bit 20 makes a clustering group at depth 20
        // whose direct child the target is
        List<(byte[], byte[]?)> writes = BranchingUnder(24);
        byte[] splitStem = new byte[Stem.Length];
        splitStem[2] = 0x08; // bit 20
        (byte[], byte[]?) split = ([.. splitStem, 5], Value);

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, PbtTestFormats.Clustered(PbtGroupFormat.Interleaved));
        harness.ApplyBatch(writes);
        TrieNodeKey target = TrieNodeKey.For(24, default);
        Assert.That(harness.Nodes.Keys, Does.Contain(target));

        harness.ApplyBatch([split]);
        Assert.That(harness.Nodes.Keys, Does.Contain(TrieNodeKey.For(20, default)), "the split group is where the run ended");
        Assert.That(harness.Nodes.ContainsKey(target), Is.False, "and it has taken its target's bytes off that key");
        Assert.That(harness.FlattenedNodes().Keys, Does.Contain(target));

        // deleting the stem again leaves the split group with one child, so it folds back into a run —
        // and the target it was holding needs its key back
        harness.ApplyBatch([(split.Item1, null)]);
        Assert.That(harness.Nodes.Keys, Does.Contain(target), "the collapse hands the target back its key");
        Assert.That(harness.Nodes.ContainsKey(TrieNodeKey.For(20, default)), Is.False);
        AssertMatchesFreshRebuild(harness, writes);

        // both crossings copy the target's bytes into memory of their own, which is a lease to balance
        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(TrackingMemoryProvider.CountUnreleased(harness.HandedOut), Is.Zero, "and every buffer a read was handed");
    }

    /// <summary>
    /// A run is a hundred-odd bytes, far too little to be worth a key: the boundary slot it hangs from
    /// holds its whole encoding, whether or not that group also clusters its child groups — which a run is
    /// none of, being no blob of the store's at all.
    /// </summary>
    [TestCase(0, TestName = "ARunUnderTheRoot_IsHeldByIt")]
    [TestCase(4, TestName = "ARunUnderAClusteringGroup_IsHeldBesideTheChildrenItClusters")]
    public void ARunHasNoKeyOfItsOwn(int parentDepth)
    {
        List<(byte[], byte[]?)> writes = RunUnder(parentDepth);

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtTestFormats.Clustered(PbtGroupFormat.Interleaved));
        harness.ApplyBatch(writes);

        // the corridor is the all-zero path, so the run hangs from its parent's first slot
        TrieNodeKey parent = TrieNodeKey.For(parentDepth, default);
        TrieNodeKey runKey = parent.ChildGroup(0, Layout.LevelsPerGroup);
        byte[] parentBlob = harness.Nodes[parent];
        PbtNodeCluster cluster = PbtNodeCluster.Decode<Layout>(parentBlob, out PbtTrieNodeGroup<Layout> group);

        Assert.That(harness.Nodes.ContainsKey(runKey), Is.False, "a run is no blob of the store's");
        Assert.That(PbtNodeChain.IsChain(harness.FlattenedNodes()[runKey]), "yet the trie holds it, one group below its parent");
        Assert.That(group.KindAt(PbtLayout.TrieNodeGroupBoundarySlotPosition(0)), Is.EqualTo(PbtTrieNodeGroup.NodeKind.Chain));
        Assert.That(parentBlob[cluster.Child(0, group)], Is.Empty, "a run is the group's own entry, never one of the children it clusters");
        Assert.That(
            PbtNodeCluster.HoldsChildren(parentBlob), Is.EqualTo(Layout.IsClusteringDepth(parentDepth)),
            "the group beside it is clustered or keyed by depth, as it would be with no run there");
        Assert.That(harness.Nodes.Keys, Does.Contain(TrieNodeKey.For(RunTargetDepth, default)), "the run's target keeps a key of its own");

        AssertMatchesFreshRebuild(harness, writes);
    }

    /// <summary>
    /// A clustering group collapses into a run once one boundary is left, and the survivor's bytes go
    /// with it: they move out into a blob of their own under the survivor's key. The bytes are in the
    /// group's own blob either way, but which copy is live depends on whether the same batch rebuilt
    /// them — here it did, the other survivor being covered by
    /// <see cref="ARunSplittingOntoItsTarget_AdoptsItAndGivesItBack"/>.
    /// </summary>
    [Test]
    public void AClusterCollapsing_HandsOverTheSurvivorItJustRebuilt()
    {
        // two stems apiece under the depth-4 group's slots 0 and 1, each pair parting at depth 8 so the
        // slot roots a group there; one more stem parts at depth 0, so the group at depth 4 is real
        (byte[] Key, byte[]? Value) leftLow = (TwoBytePrefixed(0x00, 0x00), Value);
        (byte[] Key, byte[]? Value) leftHigh = (TwoBytePrefixed(0x00, 0x10), Value);
        (byte[] Key, byte[]? Value) rightLow = (TwoBytePrefixed(0x01, 0x00), Value);
        List<(byte[], byte[]?)> writes =
        [
            leftLow, leftHigh, rightLow,
            (TwoBytePrefixed(0x01, 0x10), Value),
            (TwoBytePrefixed(0x10, 0x00), Value),
        ];

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, PbtTestFormats.Clustered(PbtGroupFormat.Interleaved));
        harness.ApplyBatch(writes);

        TrieNodeKey clusterKey = TrieNodeKey.For(Layout.LevelsPerGroup, default);
        Assert.That(PbtNodeCluster.HoldsChildren(harness.Nodes[clusterKey]), "the depth-4 group holds both its children");

        // empty slot 0 and rewrite under slot 1 in one batch: the group is left with the one boundary,
        // and its bytes are the ones this very batch folded rather than the ones it read
        List<(byte[], byte[]?)> survivors = [rightLow with { Value = Other }, .. writes[3..]];
        harness.ApplyBatch([(leftLow.Key, null), (leftHigh.Key, null), .. survivors[..1]]);

        Assert.That(harness.Nodes.ContainsKey(clusterKey), Is.False, "the collapsed group leaves no blob behind");
        Assert.That(
            harness.Nodes.Keys, Does.Contain(clusterKey.ChildGroup(1, Layout.LevelsPerGroup)),
            "the survivor takes back the key the cluster was holding its bytes instead of");
        AssertMatchesFreshRebuild(harness, survivors);

        Assert.That(TrackingMemoryProvider.CountUnreleased(provider.Rented), Is.Zero, "every rented buffer must end up fully released");
        Assert.That(TrackingMemoryProvider.CountUnreleased(harness.HandedOut), Is.Zero, "and every buffer a read was handed");
    }

    /// <summary>A tree key whose stem is zero but for its first two bytes, which is what puts it in a given depth-4 and depth-8 slot.</summary>
    private static byte[] TwoBytePrefixed(byte first, byte second)
    {
        byte[] key = new byte[Stem.Length + 1];
        key[0] = first;
        key[1] = second;
        key[^1] = 5;
        return key;
    }

    /// <summary>The bit two stems part at, which is where every run these build lands.</summary>
    private const int RunTargetDepth = 24;

    /// <summary>
    /// Writes whose trie branches at every group depth down to <paramref name="parentDepth"/> — each of
    /// those groups also rooting a child group beside the branch — and then runs single-child from one
    /// group below it to <see cref="RunTargetDepth"/>, where two stems part.
    /// </summary>
    private static List<(byte[], byte[]?)> RunUnder(int parentDepth)
    {
        byte[] corridor = new byte[Stem.Length];
        byte[] parted = new byte[Stem.Length];
        SetBit(parted, RunTargetDepth);

        List<(byte[], byte[]?)> writes = [([.. corridor, 5], Value), ([.. parted, 5], Value)];
        for (int depth = 0; depth <= parentDepth; depth += Layout.LevelsPerGroup)
        {
            // a pair parting one group below the branch, so the slot beside the run roots a group
            byte[] sibling = new byte[Stem.Length];
            SetBit(sibling, depth + Layout.LevelsPerGroup - 1);
            byte[] partner = (byte[])sibling.Clone();
            SetBit(partner, depth + 2 * Layout.LevelsPerGroup - 1);
            writes.Add(([.. sibling, 5], Value));
            writes.Add(([.. partner, 5], Value));
        }

        return writes;
    }

    /// <summary>
    /// Stems sharing every bit above <paramref name="branchDepth"/>, branching into two boundary slots
    /// there and into two more one group below, so a run reaches the group at that depth and that group
    /// roots a child group of its own.
    /// </summary>
    private static List<(byte[], byte[]?)> BranchingUnder(int branchDepth)
    {
        byte[] apart = new byte[Stem.Length];
        SetBit(apart, branchDepth);

        byte[] left = new byte[Stem.Length];
        SetBit(left, branchDepth + Layout.LevelsPerGroup);

        byte[] right = new byte[Stem.Length];
        SetBit(right, branchDepth + Layout.LevelsPerGroup + 1);

        return [([.. apart, 5], Value), ([.. left, 5], Value), ([.. right, 5], Value)];
    }

    private static void SetBit(byte[] stem, int bit) => stem[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));

    /// <summary>
    /// The store must be what folding the surviving writes from nothing produces, blob for blob: a
    /// node left at the wrong key reads back the same but is not the same store.
    /// </summary>
    private static void AssertMatchesFreshRebuild(PbtTreeHarness harness, List<(byte[], byte[]?)> writes)
    {
        PbtTreeHarness fresh = new(PooledRefCountingMemoryProvider.Instance, harness.WriteFormat);
        fresh.ApplyBatch(writes);

        Assert.That(harness.Nodes.Keys, Is.EquivalentTo(fresh.Nodes.Keys));
        foreach ((TrieNodeKey key, byte[] expected) in fresh.Nodes) Assert.That(harness.Nodes[key], Is.EqualTo(expected), $"node {key}");
    }
}
