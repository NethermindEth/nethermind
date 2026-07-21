// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Where the updater puts each blob, as against what the trie holds: a group at a
/// <see cref="PbtTrieNodeWrapper.WrapsChildren"/> depth holds its children's blobs, so their keys hold
/// nothing, and a node moving across that boundary has to move its bytes with it.
/// </summary>
/// <remarks>
/// The roots and the canonical shape are <see cref="StemTrieTests"/>'s business, and every scenario
/// here goes through <c>AssertStoreMatchesFreshRebuild</c> there too by construction; what these pin
/// is the placement alone, which a byte-identical store would agree on however wrong it was.
/// </remarks>
public class PbtNodeWrappingTests
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");

    /// <summary>
    /// Every other group level is reached without a lookup of its own. A spine of stems parting one
    /// nibble deeper each time puts a branching group at every group depth, and half of them — the
    /// odd ones, depth 0 not wrapping — have no key at all.
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

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.Interleaved);
        harness.ApplyBatch(writes);

        int[] nodeDepths = [.. harness.FlattenedNodes().Keys.Select(key => (int)key.Depth).Order()];
        Assert.That(nodeDepths, Is.EqualTo(Enumerable.Range(0, Levels).Select(level => level * PbtTrieNodeGroup.LevelsPerGroup)));
        Assert.That(
            harness.Nodes.Keys.Select(key => (int)key.Depth).Order(),
            Is.EqualTo(nodeDepths.Where(depth => depth == 0 || !PbtTrieNodeWrapper.WrapsChildren(depth - PbtTrieNodeGroup.LevelsPerGroup))),
            "a group whose parent wraps has no key of its own; the root has no parent");

        // and the bytes at the key that does hold it are the child's own encoding, verbatim
        TrieNodeKey wrapped = new TrieNodeKey(PbtTrieNodeGroup.LevelsPerGroup, default).ChildGroup(0);
        byte[] blob = harness.Nodes[new TrieNodeKey(PbtTrieNodeGroup.LevelsPerGroup, default)];
        PbtTrieNodeWrapper wrapper = PbtTrieNodeWrapper.Decode(blob, out PbtTrieNodeGroup group);
        Assert.That(blob[wrapper.Child(0, group)], Is.EqualTo(harness.FlattenedNodes()[wrapped]));
    }

    /// <summary>
    /// A run lands wherever the trie next branches, so its target's depth decides for itself whether
    /// that group holds its own children — which is the whole reason the parity is the depth's rather
    /// than the descent's.
    /// </summary>
    [TestCase(20, false, TestName = "ChainTargetAtAWrappingDepth_HoldsItsChild")]
    [TestCase(24, true, TestName = "ChainTargetBelowOne_LeavesItsChildAKey")]
    public void ChainTarget_WrapsByItsOwnDepth(int targetDepth, bool childHasKey)
    {
        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.Interleaved);
        harness.ApplyBatch(BranchingUnder(targetDepth));

        TrieNodeKey target = TrieNodeKey.For(targetDepth, default);
        Assert.That(harness.Nodes.Keys, Does.Contain(target), "a run's target always keeps a key of its own");
        Assert.That(harness.Nodes.ContainsKey(target.ChildGroup(0)), Is.EqualTo(childHasKey));
        Assert.That(harness.FlattenedNodes().Keys, Does.Contain(target.ChildGroup(0)), "either way the trie holds it");
    }

    /// <summary>
    /// A run splitting onto its own target makes a group where there was none, and where that group
    /// wraps, the target must move out of the key the run left it under and into the new blob — then
    /// back out again when deleting the stem collapses the group back into the run.
    /// </summary>
    [Test]
    public void ARunSplittingOntoItsTarget_AdoptsItAndGivesItBack()
    {
        // the run reaches depth 24, so a stem parting at bit 20 makes a wrapping group at depth 20
        // whose direct child the target is
        List<(byte[], byte[]?)> writes = BranchingUnder(24);
        byte[] splitStem = new byte[Stem.Length];
        splitStem[2] = 0x08; // bit 20
        (byte[], byte[]?) split = ([.. splitStem, 5], Value);

        TrackingMemoryProvider provider = new();
        PbtTreeHarness harness = new(provider, PbtGroupFormat.Interleaved);
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
    /// Stems sharing every bit above <paramref name="branchDepth"/>, branching into two boundary slots
    /// there and into two more one group below, so a run reaches the group at that depth and that group
    /// roots a child group of its own.
    /// </summary>
    private static List<(byte[], byte[]?)> BranchingUnder(int branchDepth)
    {
        byte[] apart = new byte[Stem.Length];
        SetBit(apart, branchDepth);

        byte[] left = new byte[Stem.Length];
        SetBit(left, branchDepth + PbtTrieNodeGroup.LevelsPerGroup);

        byte[] right = new byte[Stem.Length];
        SetBit(right, branchDepth + PbtTrieNodeGroup.LevelsPerGroup + 1);

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
