// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// Gloas <c>is_data_available</c> for an envelope's block: the columns this node custodies and samples,
/// each verified against the commitments of the bid the block committed, which the sidecars do not carry.
/// </summary>
public class GloasCustodySamplingAvailabilityTests
{
    private const ulong BlockSlot = 1;
    private const string Peer = "peer";
    private static readonly Hash256 NodeId = new([.. Enumerable.Repeat((byte)0x37, 32)]);
    private static readonly NodeColumnCustody Custody = new(NodeId, Eip7594DasConstants.CustodyRequirement);
    private static BeaconChainSpec Spec => ImportableBlobBlock.FuluFromGenesis;

    [Test]
    public void A_bid_without_blob_commitments_is_available_without_identity_or_columns()
    {
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(null, new DataColumnSidecarPool(), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid([])), Is.True);
    }

    [Test]
    public void Fails_closed_while_the_node_identity_is_unknown()
    {
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(null, PoolHolding(All()), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid()), Is.False);
    }

    [Test]
    public void Available_once_every_custody_and_sampled_column_is_held_and_verified()
    {
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, PoolHolding(Custody.SampledColumns), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid()), Is.True);
    }

    private static IEnumerable<TestCaseData> MissingColumns()
    {
        yield return new TestCaseData(Custody.CustodyColumns[^1]).SetArgDisplayNames("a custody column");
        yield return new TestCaseData(Custody.SampledColumns.First(c => !Custody.CustodyColumns.Contains(c))).SetArgDisplayNames("a sampled-only column");
    }

    [TestCaseSource(nameof(MissingColumns))]
    public void Unavailable_when_one_column_is_missing(ulong missing)
    {
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, PoolHolding(Custody.SampledColumns.Where(c => c != missing)), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid()), Is.False);
    }

    [Test]
    public void Columns_held_for_another_block_do_not_count()
    {
        Hash256 otherRoot = new([.. Enumerable.Repeat((byte)0xB2, 32)]);
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, PoolHolding(Custody.SampledColumns, otherRoot), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid()), Is.False);
    }

    [Test]
    public void A_held_column_with_a_tampered_cell_does_not_count()
    {
        DataColumnSidecarPool pool = PoolHolding(Custody.SampledColumns);
        pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, Custody.SampledColumns[0], out DataColumnSidecarGloas? held);
        byte[] cell = held!.Column![0].AsSpan().ToArray();
        cell[^1] ^= 0x01;
        held.Column[0] = SszBlobCell.FromSpan(cell);
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, pool, ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid()), Is.False, "a served entry is re-proved at every check");
    }

    /// <summary>The sidecars carry no commitments, so only a rule reading the bid's can see this swap.</summary>
    [Test]
    public void Held_columns_do_not_count_against_a_bid_committing_other_commitments()
    {
        SszKzgCommitment[] commitments = DataColumnSidecarGloasTestFixture.Commitments();
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, PoolHolding(Custody.SampledColumns), ClockAtEpoch(0));

        Assert.That(isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid([commitments[1], commitments[0]])), Is.False);
    }

    [Test]
    public void Pending_columns_that_verify_against_the_bid_count_and_become_served()
    {
        DataColumnSidecarPool pool = new();
        foreach (ulong column in Custody.SampledColumns)
        {
            pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, BlockSlot), Peer);
        }

        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, pool, ClockAtEpoch(0));
        bool available = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(Custody.SampledColumns.All(c => pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, c, out _)), Is.True, "verified sidecars are served");
            Assert.That(pool.PendingGloasCount, Is.Zero);
        }
    }

    [Test]
    public void A_pending_column_that_fails_against_the_bid_is_not_served()
    {
        DataColumnSidecarPool pool = PoolHolding(Custody.SampledColumns.Skip(1));
        ulong column = Custody.SampledColumns[0];
        DataColumnSidecarGloas pending = DataColumnSidecarGloasTestFixture.BuildSidecar(column, BlockSlot);
        pending.KzgProofs = [pending.KzgProofs![1], pending.KzgProofs[0]];
        pool.AddPendingGloas(pending, Peer);
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, pool, ClockAtEpoch(0));

        bool available = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.False);
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, column, out _), Is.False, "an unverified sidecar must never reach req/resp");
            Assert.That(pool.PendingGloasCount, Is.Zero, "a candidate that fails against the block's bid can never verify");
        }
    }

    /// <summary>
    /// Slot is not bound by KZG, and a pending sidecar predates its block, so no gossip check has matched
    /// its slot to the block's; served, it would claim a slot its block does not occupy.
    /// </summary>
    [Test]
    public void A_pending_column_naming_another_slot_is_not_served()
    {
        DataColumnSidecarPool pool = PoolHolding(Custody.SampledColumns.Skip(1));
        ulong column = Custody.SampledColumns[0];
        const ulong forgedSlot = BlockSlot + 1;
        pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, forgedSlot), Peer);
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, pool, ClockAtEpoch(0));

        bool available = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.False);
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, column, out _), Is.False);
            Assert.That(pool.PendingGloasCount, Is.Zero);
        }
    }

    /// <summary>
    /// Pending sidecars are unverified and keyed only by the root and column they name, so a forgery from
    /// one peer must not hide a valid sidecar from another, whichever arrives first.
    /// </summary>
    [Test]
    public void A_forged_pending_column_does_not_hide_a_valid_one_from_another_peer([Values] bool forgedFirst)
    {
        DataColumnSidecarPool pool = PoolHolding(Custody.SampledColumns.Skip(1));
        ulong column = Custody.SampledColumns[0];
        DataColumnSidecarGloas valid = DataColumnSidecarGloasTestFixture.BuildSidecar(column, BlockSlot);
        DataColumnSidecarGloas forged = DataColumnSidecarGloasTestFixture.BuildSidecar(column, BlockSlot);
        forged.KzgProofs = [forged.KzgProofs![1], forged.KzgProofs[0]];
        if (forgedFirst) pool.AddPendingGloas(forged, "forging peer");
        pool.AddPendingGloas(valid, Peer);
        if (!forgedFirst) pool.AddPendingGloas(forged, "forging peer");
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(Custody, pool, ClockAtEpoch(0));

        bool available = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(available, Is.True);
            Assert.That(pool.TryGetGloas(DataColumnSidecarGloasTestFixture.BlockRoot, column, out DataColumnSidecarGloas? served) ? served : null, Is.SameAs(valid));
            Assert.That(pool.PendingGloasCount, Is.Zero);
        }
    }

    /// <summary>
    /// The window is Fulu's wall-clock window, keyed on the bid's slot and re-read at every check: the
    /// block stays inside it until the clock is more than <c>MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS</c> epochs past it.
    /// </summary>
    [Test]
    public void The_window_follows_the_wall_clock_at_every_check()
    {
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)Spec.GenesisTime).UtcDateTime);
        Func<Hash256, ExecutionPayloadBid, bool> isDataAvailable = CreateRule(null, new DataColumnSidecarPool(), new SlotClock(Spec, timestamper));
        TimeSpan epoch = TimeSpan.FromSeconds(Spec.SlotsPerEpoch * Spec.SecondsPerSlot);

        bool atGenesis = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());
        timestamper.Add(epoch * Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests);
        bool atTheWindowEdge = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());
        timestamper.Add(epoch);
        bool oneEpochPast = isDataAvailable(DataColumnSidecarGloasTestFixture.BlockRoot, Bid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atGenesis, Is.False, "inside the window, with no identity, the rule fails closed");
            Assert.That(atTheWindowEdge, Is.False);
            Assert.That(oneEpochPast, Is.True, "the network no longer guarantees to serve these columns, so none can be demanded");
        }
    }

    /// <summary>Typed as the <see cref="ExecutionPayloadEnvelopeImporter"/> delegate, so the rule's signature cannot drift from it.</summary>
    private static Func<Hash256, ExecutionPayloadBid, bool> CreateRule(NodeColumnCustody? custody, DataColumnSidecarPool pool, SlotClock clock) =>
        new GloasCustodySamplingAvailability(new FixedCustodySource(custody), pool, clock, Spec).IsDataAvailable;

    private static ExecutionPayloadBid Bid(SszKzgCommitment[]? commitments = null) => new()
    {
        Slot = BlockSlot,
        BlobKzgCommitments = commitments ?? DataColumnSidecarGloasTestFixture.Commitments(),
    };

    private static DataColumnSidecarPool PoolHolding(IEnumerable<ulong> columns, Hash256? blockRoot = null)
    {
        DataColumnSidecarPool pool = new();
        foreach (ulong column in columns)
        {
            pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, BlockSlot, blockRoot));
        }

        return pool;
    }

    private static SlotClock ClockAtEpoch(ulong epoch) =>
        new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + epoch * Spec.SlotsPerEpoch * Spec.SecondsPerSlot)).UtcDateTime));

    private static IEnumerable<ulong> All() => Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c);

    private sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }
}
