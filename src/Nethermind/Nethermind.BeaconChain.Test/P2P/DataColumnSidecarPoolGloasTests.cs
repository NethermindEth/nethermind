// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The pool's Gloas members: keyed by the sidecar's own (beacon_block_root, index) and slot, kept apart
/// from the Fulu maps, and never serving a pending, unverified sidecar.
/// </summary>
public class DataColumnSidecarPoolGloasTests
{
    private const ulong Column = 3;
    private const ulong Slot = 7;

    [Test]
    public void A_gloas_sidecar_is_found_by_root_and_by_slot_under_its_own_fields()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);

        pool.AddGloas(sidecar);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out DataColumnSidecarGloas? byRoot) ? byRoot : null, Is.SameAs(sidecar));
            Assert.That(pool.TryGetGloas(Slot, Column, out DataColumnSidecarGloas? bySlot) ? bySlot : null, Is.SameAs(sidecar));
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column + 1, out _), Is.False, "the column is part of the key");
        }
    }

    /// <summary>Both at one slot, so a shared slot index would let the later add hide the other fork's sidecar.</summary>
    [Test]
    public void Gloas_and_fulu_sidecars_at_one_slot_do_not_see_each_other()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas gloas = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        DataColumnSidecar fulu = DataColumnSidecarTestFixture.BuildValidSidecar(Column, Slot);
        Hash256 fuluRoot = new(new byte[32]);
        pool.AddGloas(gloas);
        pool.Add(fuluRoot, Slot, fulu);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetGloas(Slot, Column, out DataColumnSidecarGloas? gloasBySlot) ? gloasBySlot : null, Is.SameAs(gloas));
            Assert.That(pool.TryGet(Slot, Column, out DataColumnSidecar? fuluBySlot) ? fuluBySlot : null, Is.SameAs(fulu));
            Assert.That(pool.TryGet(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _), Is.False);
            Assert.That(pool.TryGetGloas(fuluRoot, Column, out _), Is.False);
        }
    }

    [Test]
    public void A_pending_sidecar_is_never_served_until_added_as_verified()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);

        pool.AddPendingGloas(sidecar);
        bool servedWhilePending = pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _) || pool.TryGetGloas(Slot, Column, out _);
        bool heldAsPending = pool.TryGetPendingGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _);
        pool.AddGloas(sidecar);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedWhilePending, Is.False);
            Assert.That(heldAsPending, Is.True);
            Assert.That(pool.TryGetGloas(Slot, Column, out _), Is.True);
            Assert.That(pool.TryGetPendingGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _), Is.False, "a verified sidecar leaves the pending map");
        }
    }

    [Test]
    public void A_sidecar_without_a_block_root_is_refused([Values] bool pending)
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        sidecar.BeaconBlockRoot = null;

        Assert.That(() =>
        {
            if (pending) pool.AddPendingGloas(sidecar);
            else pool.AddGloas(sidecar);
        }, Throws.ArgumentException);
    }

    [Test]
    public void The_gloas_slot_index_does_not_grow_past_capacity()
    {
        const int capacity = 4;
        DataColumnSidecarPool pool = new(capacity);

        for (ulong slot = 0; slot < capacity + 20; slot++)
        {
            pool.AddGloas(new DataColumnSidecarGloas { Index = 0, Column = [], KzgProofs = [], Slot = slot, BeaconBlockRoot = RootFor(slot) });
        }

        Assert.That(pool.GloasSlotIndexCount, Is.LessThanOrEqualTo(capacity));
    }

    [Test]
    public void The_pending_map_is_bounded_below_the_served_capacity()
    {
        DataColumnSidecarPool pool = new();

        for (ulong i = 0; i <= DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            pool.AddPendingGloas(new DataColumnSidecarGloas { Index = 0, Column = [], KzgProofs = [], Slot = i, BeaconBlockRoot = RootFor(i) });
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetPendingGloas(RootFor(0), 0, out _), Is.False, "the oldest unverified sidecar is evicted");
            Assert.That(pool.TryGetPendingGloas(RootFor(DataColumnSidecarPool.MaxPendingGloasSidecars), 0, out _), Is.True);
        }
    }

    private static Hash256 RootFor(ulong value)
    {
        byte[] bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, value + 1);
        return new Hash256(bytes);
    }
}
