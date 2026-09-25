// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private const string Peer = "peer";

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

        pool.AddPendingGloas(sidecar, Peer);
        pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot), "other peer");
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
            if (pending) pool.AddPendingGloas(sidecar, Peer);
            else pool.AddGloas(sidecar);
        }, Throws.ArgumentException);
    }

    [Test]
    public void A_peer_replaces_only_its_own_candidate_for_a_root_and_column()
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas other = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        DataColumnSidecarGloas earlier = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        DataColumnSidecarGloas later = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);

        pool.AddPendingGloas(other, "other peer");
        pool.AddPendingGloas(earlier, Peer);
        pool.AddPendingGloas(later, Peer);

        Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Is.EquivalentTo(new[] { other, later }));
    }

    [Test]
    public void The_pending_total_is_bounded_and_evicts_the_oldest([Values] bool onePeer)
    {
        DataColumnSidecarPool pool = new();

        for (ulong i = 0; i <= DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            pool.AddPendingGloas(new DataColumnSidecarGloas { Index = 0, Column = [], KzgProofs = [], Slot = i, BeaconBlockRoot = RootFor(i) }, onePeer ? Peer : $"peer {i}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.PendingGloasCount, Is.EqualTo(DataColumnSidecarPool.MaxPendingGloasSidecars));
            Assert.That(pool.GetPendingGloas(RootFor(0), 0), Is.Empty, "the oldest unverified sidecar is evicted");
            Assert.That(pool.GetPendingGloas(RootFor(DataColumnSidecarPool.MaxPendingGloasSidecars), 0), Has.Length.EqualTo(1));
        }
    }

    /// <summary>A flood of forgeries, for this (root, column) or for others, must not cost another peer its candidate.</summary>
    [Test]
    public void A_flooding_peer_cannot_evict_another_peers_candidate([Values] bool sameRootAndColumn)
    {
        DataColumnSidecarPool pool = new();
        DataColumnSidecarGloas honest = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        pool.AddPendingGloas(honest, "honest peer");

        for (ulong i = 0; i < 2 * DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            Hash256 root = sameRootAndColumn ? DataColumnSidecarGloasTestFixture.BlockRoot : RootFor(i);
            pool.AddPendingGloas(new DataColumnSidecarGloas { Index = Column, Column = [], KzgProofs = [], Slot = Slot, BeaconBlockRoot = root }, "flooding peer");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Does.Contain(honest));
            Assert.That(pool.PendingGloasCount, Is.LessThanOrEqualTo(DataColumnSidecarPool.MaxPendingGloasSidecars));
        }
    }

    /// <summary>A peer at the bound that adds one more gives up its own candidate, not one of a peer holding as many.</summary>
    [Test]
    public void A_flooding_peer_loses_ties_with_a_peer_holding_as_many_candidates()
    {
        DataColumnSidecarPool pool = new();
        const ulong half = DataColumnSidecarPool.MaxPendingGloasSidecars / 2;
        for (ulong i = 0; i < half; i++)
        {
            pool.AddPendingGloas(Forged(RootFor(i)), "honest peer");
        }

        for (ulong i = half; i <= 2 * half; i++)
        {
            pool.AddPendingGloas(Forged(RootFor(i)), "flooding peer");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingFor(pool, RootFor(0)), Has.Length.EqualTo(1), "the honest peer's oldest candidate stays");
            Assert.That(PendingFor(pool, RootFor(half)), Is.Empty, "the flooding peer pays for its own add");
        }
    }

    /// <summary>Fresh peer ids, one candidate each, must not evict a candidate that arrived after theirs.</summary>
    [Test]
    public void Fresh_peer_ids_evict_the_oldest_candidate_not_the_newest()
    {
        DataColumnSidecarPool pool = new();
        for (ulong i = 0; i < DataColumnSidecarPool.MaxPendingGloasSidecars; i++)
        {
            pool.AddPendingGloas(Forged(RootFor(i)), $"sybil {i}");
        }

        DataColumnSidecarGloas honest = DataColumnSidecarGloasTestFixture.BuildSidecar(Column, Slot);
        pool.AddPendingGloas(honest, "honest peer");
        for (ulong i = 0; i < 8; i++)
        {
            pool.AddPendingGloas(Forged(RootFor(DataColumnSidecarPool.MaxPendingGloasSidecars + i)), $"fresh sybil {i}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingFor(pool, DataColumnSidecarGloasTestFixture.BlockRoot), Does.Contain(honest));
            Assert.That(PendingFor(pool, RootFor(0)), Is.Empty, "the oldest candidate is the one evicted");
            Assert.That(pool.PendingGloasCount, Is.EqualTo(DataColumnSidecarPool.MaxPendingGloasSidecars));
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
