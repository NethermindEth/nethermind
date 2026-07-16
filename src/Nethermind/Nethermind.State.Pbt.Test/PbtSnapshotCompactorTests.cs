// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotCompactorTests
{
    private static readonly Stem StemA = new(new byte[31]);

    private readonly PbtResourcePool _pool = new(new PbtConfig());

    /// <summary>
    /// The merge runs oldest to newest, so the newest layer's write for a key must survive. Reversing
    /// the walk — or feeding the chain in the wrong order — silently persists the stale value instead
    /// of the current one, with no exception anywhere.
    /// </summary>
    [Test]
    public void Compact_TakesTheNewestLayersValue_ForEveryKindOfKey()
    {
        Address address = TestItem.AddressA;
        TrieNodeKey nodeKey = new(0, StemA);

        Account olderAccount = Build.An.Account.WithNonce(1).TestObject;
        Account newerAccount = Build.An.Account.WithNonce(2).TestObject;

        PbtSnapshotContent older = new();
        older.Accounts[address] = olderAccount;
        older.Slots[(address, (UInt256)7)] = Word(0x11);
        older.LeafBlobs[StemA] = [0x11];
        older.TrieNodes[nodeKey] = [0x11];

        PbtSnapshotContent newer = new();
        newer.Accounts[address] = newerAccount;
        newer.Slots[(address, (UInt256)7)] = Word(0x22);
        newer.LeafBlobs[StemA] = [0x22];
        newer.TrieNodes[nodeKey] = [0x22];

        using PbtSnapshot merged = Compact(older, newer);

        Assert.That(merged.Content.Accounts[address], Is.SameAs(newerAccount));
        Assert.That(merged.Content.Slots[(address, (UInt256)7)], Is.EqualTo(Word(0x22)));
        Assert.That(merged.Content.LeafBlobs[StemA], Is.EqualTo((byte[])[0x22]));
        Assert.That(merged.Content.TrieNodes[nodeKey], Is.EqualTo((byte[])[0x22]));
    }

    /// <summary>The merged layer spans the whole segment: the oldest layer's start to the newest layer's end.</summary>
    [Test]
    public void Compact_SpansTheWholeSegment()
    {
        using PbtSnapshot merged = Compact(new PbtSnapshotContent(), new PbtSnapshotContent());

        Assert.That(merged.From, Is.EqualTo(State(1)));
        Assert.That(merged.To, Is.EqualTo(State(3)));
    }

    /// <summary>Disjoint writes from either layer all survive the merge.</summary>
    [Test]
    public void Compact_UnionsDisjointWrites()
    {
        PbtSnapshotContent older = new();
        older.Slots[(TestItem.AddressA, UInt256.Zero)] = Word(0x11);
        PbtSnapshotContent newer = new();
        newer.Slots[(TestItem.AddressB, UInt256.Zero)] = Word(0x22);

        using PbtSnapshot merged = Compact(older, newer);

        Assert.That(merged.Content.Slots[(TestItem.AddressA, UInt256.Zero)], Is.EqualTo(Word(0x11)));
        Assert.That(merged.Content.Slots[(TestItem.AddressB, UInt256.Zero)], Is.EqualTo(Word(0x22)));
    }

    /// <summary>
    /// A self-destruct hides every slot written for that address in an older layer, but not one
    /// written after it, and never another address's slots.
    /// </summary>
    [Test]
    public void Compact_SelfDestruct_DropsOlderSlotsOnly()
    {
        Address destroyed = TestItem.AddressA;
        PbtSnapshotContent older = new();
        older.Slots[(destroyed, (UInt256)1)] = Word(0x11);
        older.Slots[(TestItem.AddressB, (UInt256)1)] = Word(0x11);

        PbtSnapshotContent newer = new();
        newer.SelfDestructs[destroyed] = true;
        newer.Slots[(destroyed, (UInt256)2)] = Word(0x22);

        using PbtSnapshot merged = Compact(older, newer);

        Assert.That(merged.Content.Slots.ContainsKey((destroyed, (UInt256)1)), Is.False, "a slot written before the self-destruct must not survive it");
        Assert.That(merged.Content.Slots[(destroyed, (UInt256)2)], Is.EqualTo(Word(0x22)), "a slot written after the self-destruct must survive");
        Assert.That(merged.Content.Slots[(TestItem.AddressB, (UInt256)1)], Is.EqualTo(Word(0x11)), "an unrelated address must be untouched");
        Assert.That(merged.Content.SelfDestructs.ContainsKey(destroyed), "the marker is kept so persistence still range-deletes the on-disk storage");
    }

    /// <summary>
    /// When an address is destroyed more than once, only the newest destruct counts: a slot written
    /// between the two is wiped by the later one, and only writes at or after it survive. The merge
    /// filters against that boundary rather than replaying each destruct, so the boundary has to be
    /// the last one, not the first.
    /// </summary>
    [Test]
    public void Compact_SelfDestructedTwice_KeepsOnlySlotsFromTheLastDestructOnwards()
    {
        Address destroyed = TestItem.AddressA;

        PbtSnapshotContent first = new();
        first.SelfDestructs[destroyed] = true;
        first.Slots[(destroyed, (UInt256)1)] = Word(0x11);   // survives the first destruct...

        PbtSnapshotContent second = new();
        second.Slots[(destroyed, (UInt256)2)] = Word(0x22);  // ...as does this, until

        PbtSnapshotContent third = new();
        third.SelfDestructs[destroyed] = true;               // the second destruct wipes both
        third.Slots[(destroyed, (UInt256)3)] = Word(0x33);

        using PbtSnapshot merged = Compact(first, second, third);

        Assert.That(merged.Content.Slots.ContainsKey((destroyed, (UInt256)1)), Is.False);
        Assert.That(merged.Content.Slots.ContainsKey((destroyed, (UInt256)2)), Is.False);
        Assert.That(merged.Content.Slots[(destroyed, (UInt256)3)], Is.EqualTo(Word(0x33)));
    }

    /// <summary>The merged layer's content is rented for the width actually merged, and returns there.</summary>
    [Test]
    public void Compact_ReturnsTheMergedContent_ToTheSizeClassOfTheMergedWidth()
    {
        PbtSnapshotContent merged;
        using (PbtSnapshot snapshot = Compact(new PbtSnapshotContent(), new PbtSnapshotContent()))
        {
            merged = snapshot.Content;
        }

        // two layers round up to the Compact2 class; renting there hands the very same content back
        Assert.That(_pool.GetSnapshotContent(PbtResourcePool.Usage.Compact2), Is.SameAs(merged));
    }

    [TestCase(1, PbtResourcePool.Usage.Compact2)]
    [TestCase(2, PbtResourcePool.Usage.Compact2)]
    [TestCase(3, PbtResourcePool.Usage.Compact4)]
    [TestCase(32, PbtResourcePool.Usage.Compact32)]
    [TestCase(200, PbtResourcePool.Usage.Compact256)]
    [TestCase(4096, PbtResourcePool.Usage.Compact2048)]
    public void CompactUsage_RoundsTheMergedWidthUpToItsSizeClass(int mergedLayerCount, PbtResourcePool.Usage expected) =>
        Assert.That(PbtResourcePool.CompactUsage(mergedLayerCount), Is.EqualTo(expected));

    /// <summary>Compacts <paramref name="layers"/> as consecutive blocks, oldest first.</summary>
    private PbtSnapshot Compact(params PbtSnapshotContent[] layers)
    {
        using PbtSnapshotPooledList chain = new(layers.Length);
        for (int i = 0; i < layers.Length; i++)
        {
            chain.Add(new PbtSnapshot(State((ulong)i + 1), State((ulong)i + 2), layers[i], _pool, PbtResourcePool.Usage.MainBlockProcessing));
        }

        return new PbtSnapshotCompactor(_pool).Compact(chain);
    }

    private static StateId State(ulong blockNumber)
    {
        byte[] root = new byte[32];
        root[31] = (byte)blockNumber;
        return new StateId(blockNumber, new ValueHash256(root));
    }

    private static EvmWord Word(byte marker) => EvmWordSlot.FromStripped([marker]);
}
