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

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// The two group encodings describe the same trie, so they interoperate: either reads either, both
/// fold to the same root, and a store written across a format switch stays correct — the guarantee
/// that lets the <c>InterleaveTrieNodeLevels</c> config flip without a migration.
/// </summary>
public class PbtFormatInteropTests
{
    private static readonly byte[] Value = Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly byte[] Rewritten = Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222");

    /// <summary>The same writes fold to the same root and the same node keys whichever format stores them.</summary>
    [Test]
    public void BothFormats_FoldToTheSameRoot()
    {
        List<(byte[], byte[]?)> writes = RandomWrites(seed: 7, count: 400);

        PbtTreeHarness everyLevel = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.EveryLevel);
        PbtTreeHarness interleaved = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.Interleaved);

        ValueHash256 rootA = everyLevel.ApplyBatch(writes);
        ValueHash256 rootB = interleaved.ApplyBatch(writes);

        Assert.That(rootB, Is.EqualTo(rootA), "both formats fold to the same root");
        Assert.That(rootB, Is.EqualTo(ReferenceRoot(writes)), "and it is the EIP-8297 reference root");
        Assert.That(interleaved.Nodes.Keys, Is.EquivalentTo(everyLevel.Nodes.Keys), "interleaving changes bytes, not the node set");
        Assert.That(TotalNodeBytes(interleaved), Is.LessThan(TotalNodeBytes(everyLevel)), "and it stores fewer of them");
    }

    /// <summary>
    /// A group first written every-level and then rewritten interleaved must come out byte-identical to
    /// one folded interleaved from scratch — the copy-verbatim path must refold across the format
    /// change rather than splice old bytes into the new encoding.
    /// </summary>
    [TestCase(PbtGroupFormat.EveryLevel, PbtGroupFormat.Interleaved)]
    [TestCase(PbtGroupFormat.Interleaved, PbtGroupFormat.EveryLevel)]
    public void MixedFormatRewrite_MatchesAFreshFoldInTheNewFormat(PbtGroupFormat initial, PbtGroupFormat then)
    {
        // sixteen stems on the boundary slots of one depth-4 group: it branches sixteen ways, so a
        // single-slot rewrite leaves whole clean subtrees for the copy-verbatim path to take
        List<(byte[], byte[]?)> writes = [];
        for (byte slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++) writes.Add((BoundaryKey(slot), Value));

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, initial);
        harness.ApplyBatch(writes);

        harness.WriteFormat = then;
        writes[3] = (writes[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([writes[3]]);

        // a fresh fold of the surviving state entirely in the new format
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

    /// <summary>
    /// A store written every-level keeps reading after the format flips: further writes fold to the
    /// reference root, a group a batch rewrites is converted, and one no batch touches is left as it was.
    /// </summary>
    [Test]
    public void OldFormatStore_ReadsAndConvertsOnlyWhatIsRewritten()
    {
        // two disjoint dense groups under the root, so a later write can touch one and leave the other
        List<(byte[], byte[]?)> groupA = [];
        List<(byte[], byte[]?)> groupB = [];
        for (byte slot = 0; slot < PbtTrieNodeGroup.BoundarySlots; slot++)
        {
            groupA.Add((BoundaryKey(0, slot), Value));
            groupB.Add((BoundaryKey(1, slot), Value));
        }

        PbtTreeHarness harness = new(PooledRefCountingMemoryProvider.Instance, PbtGroupFormat.EveryLevel);
        harness.ApplyBatch([.. groupA, .. groupB]);
        TrieNodeKey keyA = TrieNodeKey.Root.ChildGroup(0);
        TrieNodeKey keyB = TrieNodeKey.Root.ChildGroup(1);
        Assert.That(PbtTrieNodeGroup.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel));

        // flip to interleaved and rewrite one slot of group A only
        harness.WriteFormat = PbtGroupFormat.Interleaved;
        groupA[3] = (groupA[3].Item1, Rewritten);
        ValueHash256 root = harness.ApplyBatch([groupA[3]]);

        Assert.That(root, Is.EqualTo(ReferenceRoot([.. groupA, .. groupB])), "the old-format store still reads correctly");
        Assert.That(PbtTrieNodeGroup.Decode(harness.Nodes[keyA]).Format, Is.EqualTo(PbtGroupFormat.Interleaved), "a rewritten group converts");
        Assert.That(PbtTrieNodeGroup.Decode(harness.Nodes[keyB]).Format, Is.EqualTo(PbtGroupFormat.EveryLevel), "an untouched one is left as it was");
    }

    private static long TotalNodeBytes(PbtTreeHarness harness) => harness.Nodes.Values.Sum(blob => (long)blob.Length);

    /// <summary>A key on boundary slot <paramref name="slot"/> of the root's child group <paramref name="rootNibble"/>.</summary>
    private static byte[] BoundaryKey(byte rootNibble, byte slot)
    {
        byte[] key = new byte[Stem.Length + 1];
        key[0] = (byte)((rootNibble << 4) | slot); // depth-0 nibble picks the child group, depth-4 nibble the slot
        return key;
    }

    private static byte[] BoundaryKey(byte slot) => BoundaryKey(0, slot);

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
