// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>A head change reads the canonical index no higher than the clock's next slot, whatever top slot the store recorded.</summary>
public class BlockImporterCanonicalIndexBoundTests
{
    /// <summary>
    /// The recorded top slot is a stored value: a corrupted one must cost a bounded number of reads, not stall the import worker
    /// on its first head change. Entries a real previous run left above the head are still cleared.
    /// </summary>
    [Test]
    public void A_corrupted_top_slot_does_not_stall_the_head_change_and_nearby_stale_entries_are_cleared()
    {
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create(new ManualTimestamper(TickFinalityFixture.SlotStart(UnsignedChain.Create().Spec, 100)));
        fixture.ImportLongerChainA();
        fixture.Store.ApplyCanonicalIndexChanges([], ulong.MaxValue);
        CanonicalReorgFixture restarted = fixture.Restart();

        Task<Hash256> head = Task.Run(restarted.ComputeHead);

        Assert.That(head.Wait(TimeSpan.FromSeconds(30)), Is.True, "the head change is still walking toward the corrupted top slot");
        Assert.Multiple(() =>
        {
            Assert.That(head.Result, Is.EqualTo(restarted.Chain.AnchorRoot));
            Assert.That(restarted.CanonicalRoots(through: 3), Is.EqualTo(new Hash256?[] { restarted.Chain.AnchorRoot, null, null, null }));
            Assert.That(restarted.Store.GetCanonicalIndexTopSlot(), Is.EqualTo(0UL), "the recorded top slot is repaired to the head's");
        });
    }

    /// <summary>
    /// A rollback of the head can leave real entries far above it, up to the clock: a fixed distance above the head would leave them
    /// marked canonical, and the top slot repair would then forget them.
    /// </summary>
    [Test]
    public void A_stale_entry_far_above_the_new_head_but_within_the_clock_is_cleared()
    {
        const ulong staleSlot = 3 * Presets.SlotsPerHistoricalRoot;
        UnsignedChain probe = UnsignedChain.Create();
        CanonicalReorgFixture fixture = CanonicalReorgFixture.Create(new ManualTimestamper(TickFinalityFixture.SlotStart(probe.Spec, staleSlot + 10)));
        UnsignedChain.ChainBlock[] chainA = fixture.ImportLongerChainA();
        fixture.Store.ApplyCanonicalIndexChanges([(staleSlot, chainA[0].Root)], staleSlot);
        CanonicalReorgFixture restarted = fixture.Restart();

        restarted.ComputeHead();

        Assert.Multiple(() =>
        {
            Assert.That(restarted.Store.TryGetCanonicalRoot(staleSlot, out _), Is.False, "the entry above the new head is still marked canonical");
            Assert.That(restarted.CanonicalRoots(through: 3), Is.EqualTo(new Hash256?[] { restarted.Chain.AnchorRoot, null, null, null }));
        });
    }
}
