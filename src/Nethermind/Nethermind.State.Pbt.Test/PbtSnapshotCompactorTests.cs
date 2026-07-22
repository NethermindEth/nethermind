// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
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

    [Test]
    public void Compact_SpansTheWholeSegment()
    {
        using PbtSnapshot merged = Compact(new PbtSnapshotContent(), new PbtSnapshotContent());

        Assert.That(merged.From, Is.EqualTo(State(1)));
        Assert.That(merged.To, Is.EqualTo(State(3)));
    }

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
        Assert.That(merged.Content.SelfDestructs.ContainsKey(destroyed), "the marker is kept so reads of the cleared account still see a clean zero");
    }

    /// <summary>
    /// The merge filters slots against a single self-destruct boundary rather than replaying each
    /// destruct, so the boundary has to be the last one, not the first.
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

    /// <summary>
    /// The levels nest without anything coordinating them. Each block merges the width its number
    /// calls for, and because a wide window prefers the compacted edges below it, the 8-wide merge at
    /// block 8 consumes the 4-wide at 4 and the 2-wide at 6 rather than re-walking 8 base layers. A
    /// read at the head then crosses the whole span in one hop.
    /// </summary>
    [Test]
    public void Compaction_NestsItsLevels_AndTheWalkCrossesThemInOneHop()
    {
        PbtSnapshotRepository repository = new();
        PbtSnapshotCompactor compactor = NewCompactor(repository);

        for (ulong block = 1; block <= 8; block++)
        {
            PbtSnapshotContent layer = new();
            layer.Slots[(TestItem.AddressA, UInt256.Zero)] = Word((byte)block);
            repository.TryAdd(new PbtSnapshot(State(block - 1), State(block), default, layer, _pool, PbtResourcePool.Usage.MainBlockProcessing));
            compactor.DoCompactSnapshot(State(block));
        }

        // 2, 4, 6 and 8 are the aligned blocks; the odd ones merge nothing
        Assert.That(repository.CompactedCount, Is.EqualTo(4));

        using PbtSnapshotPooledList chain = new(8);
        Assert.That(repository.TryLeaseChain(State(8), State(0), chain), Is.True);
        Assert.That(chain.Count, Is.EqualTo(1), "the 8-wide layer at block 8 spans the whole window");
        Assert.That(chain[0].From, Is.EqualTo(State(0)));
        Assert.That(chain[0].Content.Slots[(TestItem.AddressA, UInt256.Zero)], Is.EqualTo(Word(8)), "and carries the newest value across it");
    }

    /// <summary>
    /// A walk that cannot use the wide edge steps through the narrow ones instead. This is why the
    /// base layers stay when a compacted layer spans them: a state between two boundaries is still
    /// reachable, and taking a wide edge that overshoots would land below the floor.
    /// </summary>
    [Test]
    public void Walk_AimingInsideACompactedSpan_FallsBackToTheBaseLayers()
    {
        PbtSnapshotRepository repository = new();
        PbtSnapshotCompactor compactor = NewCompactor(repository);

        for (ulong block = 1; block <= 4; block++)
        {
            repository.TryAdd(new PbtSnapshot(State(block - 1), State(block), default, new PbtSnapshotContent(), _pool, PbtResourcePool.Usage.MainBlockProcessing));
            compactor.DoCompactSnapshot(State(block));
        }

        // block 4 carries a 4-wide layer straight to block 0, which overshoots a floor at block 1
        using PbtSnapshotPooledList chain = new(4);
        Assert.That(repository.TryLeaseChain(State(4), State(1), chain), Is.True);
        Assert.That(chain.Count, Is.EqualTo(3), "one 2-wide layer and one base layer, not the 4-wide one");
        Assert.That(chain[0].From, Is.EqualTo(State(1)));
    }

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
            chain.Add(new PbtSnapshot(State((ulong)i + 1), State((ulong)i + 2), default, layers[i], _pool, PbtResourcePool.Usage.MainBlockProcessing));
        }

        return NewCompactor(new PbtSnapshotRepository()).Compact(chain);
    }

    private PbtSnapshotCompactor NewCompactor(PbtSnapshotRepository repository) =>
        new(_pool, new PbtCompactionSchedule(new MemDb(), Config, LimboLogs.Instance), repository, Config);

    // offset 0 pins which blocks align; left to itself it is rolled per node and the levels move
    private static readonly PbtConfig Config = new() { CompactSize = 16, CompactionOffset = 0 };

    private static StateId State(ulong blockNumber)
    {
        byte[] root = new byte[32];
        root[31] = (byte)blockNumber;
        return new StateId(blockNumber, new ValueHash256(root));
    }

    private static EvmWord Word(byte marker) => EvmWordSlot.FromStripped([marker]);
}
