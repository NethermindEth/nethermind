// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// The pool's Gloas members: keyed by the sidecar's own (beacon_block_root, index), kept apart from the
/// Fulu maps, and never serving a pending, unverified sidecar.
/// </summary>
public class DataColumnSidecarPoolGloasTests
{
    private const ulong Column = 3;
    private const ulong Slot = 7;

    [Test]
    public void A_gloas_sidecar_is_found_by_root_under_its_own_fields()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);

        pool.AddGloas(sidecar);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out DataColumnSidecarGloas? byRoot) ? byRoot : null, Is.SameAs(sidecar));
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
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out DataColumnSidecarGloas? gloasByRoot) ? gloasByRoot : null, Is.SameAs(gloas));
            Assert.That(pool.TryGet(Slot, Column, out DataColumnSidecar? fuluBySlot) ? fuluBySlot : null, Is.SameAs(fulu));
            Assert.That(pool.TryGet(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _), Is.False);
            Assert.That(pool.TryGetGloas(fuluRoot, Column, out _), Is.False);
        }
    }

    /// <summary>Competing blocks can share a slot; a per-slot root would let the later block hide the earlier one's columns.</summary>
    [Test]
    public void Competing_blocks_at_one_slot_each_keep_their_columns()
    {
        DataColumnSidecarPool pool = new();
        Hash256 competingRoot = RootFor(Slot);
        DataColumnSidecarGloas first = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        DataColumnSidecarGloas competing = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot, competingRoot);

        pool.AddGloas(first);
        pool.AddGloas(competing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out DataColumnSidecarGloas? firstHeld) ? firstHeld : null, Is.SameAs(first));
            Assert.That(pool.TryGetGloas(competingRoot, Column, out DataColumnSidecarGloas? competingHeld) ? competingHeld : null, Is.SameAs(competing));
        }
    }

    [Test]
    public void A_pending_sidecar_is_never_served_until_added_as_verified()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);

        pool.AddPendingGloas(sidecar, Slot);
        pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot), Slot);
        bool servedWhilePending = pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _);
        bool heldAsPending = PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot).Contains(sidecar);
        pool.AddGloas(sidecar);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedWhilePending, Is.False);
            Assert.That(heldAsPending, Is.True);
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Column, out _), Is.True);
            Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Is.Empty, "a verified sidecar clears every candidate for its root and column");
            Assert.That(pool.PendingGloasCount, Is.Zero);
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
            if (pending) pool.AddPendingGloas(sidecar, Slot);
            else pool.AddGloas(sidecar);
        }, Throws.ArgumentException);
    }

    /// <summary>Availability runs a KZG batch per candidate, and no arrival can tell a forgery from the genuine sidecar.</summary>
    [Test]
    public void A_root_and_column_keeps_only_its_earliest_candidates()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas[] earliest = [.. Enumerable.Range(0, DataColumnSidecarPool.MaxPendingGloasCandidatesPerKey).Select(static _ => Forged(DataColumnSidecarGloasTestFixture.BlockRoot))];
        foreach (DataColumnSidecarGloas candidate in earliest)
        {
            pool.AddPendingGloas(candidate, Slot);
        }

        bool parkedPastTheCap = pool.AddPendingGloas(Forged(DataColumnSidecarGloasTestFixture.BlockRoot), Slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parkedPastTheCap, Is.False);
            Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Is.EqualTo(earliest));
        }
    }

    /// <summary>A flood of forgeries, for this (root, column) or for others, must not cost an earlier candidate its place.</summary>
    [Test]
    public void A_flood_cannot_evict_an_earlier_candidate([Values] bool sameRootAndColumn)
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas honest = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        pool.AddPendingGloas(honest, Slot);

        for (ulong i = 0; i < 2 * DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            pool.AddPendingGloas(Forged(sameRootAndColumn ? DataColumnSidecarGloasTestFixture.BlockRoot : RootFor(i)), Slot);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Does.Contain(honest));
            Assert.That(pool.PendingGloasCount, Is.LessThanOrEqualTo(DataColumnSidecarPool.MaxPendingGloasSidecars));
        }
    }

    /// <summary>A refusing full pool must free up once its candidates' blocks can no longer be imminent, or one flood would block parking for good.</summary>
    [TestCase(Slot + 1, false, TestName = "A full pool keeps candidates through the slot after their own")]
    [TestCase(Slot + 2, true, TestName = "A full pool drops candidates two slots past their own")]
    public void A_full_pool_frees_space_only_once_its_candidates_are_stale(ulong currentSlot, bool parked)
    {
        DataColumnSidecarPool pool = new();
        for (ulong i = 0; i < DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            pool.AddPendingGloas(Forged(RootFor(i)), Slot);
        }

        DataColumnSidecarGloas later = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, currentSlot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.AddPendingGloas(later, currentSlot), Is.EqualTo(parked));
            Assert.That(PendingFor(pool, RootFor(0)), Has.Length.EqualTo(parked ? 0 : 1));
            Assert.That(pool.PendingGloasCount, Is.EqualTo(parked ? 1 : DataColumnSidecarPool.MaxPendingGloasSidecars));
        }
    }

    [Test]
    public void A_candidate_already_stale_is_not_parked()
    {
        DataColumnSidecarPool pool = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.AddPendingGloas(Forged(DataColumnSidecarGloasTestFixture.BlockRoot), Slot + 2), Is.False);
            Assert.That(pool.PendingGloasCount, Is.Zero);
        }
    }

    private static DataColumnSidecarGloas Forged(Hash256 root) =>
        new() { Index = Column, Column = [], KzgProofs = [], Slot = Slot, BeaconBlockRoot = root };

    private static DataColumnSidecarGloas[] PendingFor(DataColumnSidecarPool pool, Hash256 root) => pool.GetPendingGloas(root, Column);

    private static Hash256 RootFor(ulong value)
    {
        byte[] bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, value + 1);
        return new Hash256(bytes);
    }
}
