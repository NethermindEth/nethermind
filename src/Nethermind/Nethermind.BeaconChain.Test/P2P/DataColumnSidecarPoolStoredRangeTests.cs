// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

// fulu/p2p-interface.md DataColumnSidecarsByRange: a response must include every requested column the node custodies for each
// included block, so after a restart the stored range is checked against the slot index before any of it is claimed servable.
public class DataColumnSidecarPoolStoredRangeTests
{
    private const ulong First = 13_399_995;
    private const ulong Last = First + 10;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly IReadOnlyList<ulong> Sampled = new NodeColumnCustody(TestItem.PrivateKeyA.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement).SampledColumns;

    private static Hash256 RootAt(ulong slot) => Keccak.Compute($"canonical {slot}");

    /// <summary>A store whose canonical blocks from <see cref="First"/> to <see cref="Last"/> have every sampled column, less <paramref name="skip"/>, written through a pool.</summary>
    private static (FaultyColumnsDb Db, BeaconChainStore Store, DataColumnSidecarPool Pool) StoreRange(bool withIdentity = true, params (ulong Slot, ulong Column)[] skip)
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        if (withIdentity)
        {
            store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, TestItem.PrivateKeyA.KeyBytes);
        }

        List<(ulong Slot, Hash256? Root)> canonical = [];
        for (ulong slot = First; slot <= Last; slot++)
        {
            canonical.Add((slot, RootAt(slot)));
        }

        store.ApplyCanonicalIndexChanges(canonical, Last);
        DataColumnSidecarPool pool = new(store: store);
        for (ulong slot = First; slot <= Last; slot++)
        {
            foreach (ulong column in Sampled)
            {
                if (Array.IndexOf(skip, (slot, column)) < 0)
                {
                    pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, slot, RootAt(slot)));
                }
            }
        }

        Assert.That(store.TryGetDataColumnFloor(out ulong floor) ? floor : ulong.MaxValue, Is.EqualTo(First), "the first stored column fixes the floor");
        return (db, store, pool);
    }

    private static DataColumnSidecarPool Restart(BeaconChainStore store, SlotClock? clock = null)
    {
        DataColumnSidecarPool restarted = new(store: store, clock: clock);
        restarted.SeedCompletelyServableFloor(store.GetCanonicalIndexTopSlot());
        return restarted;
    }

    private static ulong StoredFloor(BeaconChainStore store) => store.TryGetDataColumnFloor(out ulong floor) ? floor : ulong.MaxValue;

    private static void RemoveRecord(FaultyColumnsDb db, Hash256 root, ulong column)
    {
        byte[] key = new byte[Hash256.Size + sizeof(ulong)];
        root.Bytes.CopyTo(key);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(Hash256.Size), column);
        db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).Remove(key);
    }

    private static void PutBlock(BeaconChainStore store, Hash256 root, ulong slot, bool withBlobs)
    {
        SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
        block.Message!.Body!.BlobKzgCommitments = withBlobs ? DataColumnSidecarGloasTestFixture.Commitments() : [];
        store.PutBlock(root, block);
    }

    [Test]
    public void A_column_lost_with_its_floor_before_a_restart_is_not_claimed_servable_after_it([Values] bool diskRecovers)
    {
        const ulong lostSlot = First + 4;
        ulong lostColumn = Sampled[^1];
        (FaultyColumnsDb db, BeaconChainStore store, DataColumnSidecarPool pool) = StoreRange(skip: (lostSlot, lostColumn));
        db.FailWrites = true;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(lostColumn, lostSlot, RootAt(lostSlot)));
        Assert.That((pool.EarliestCompletelyServableSlot, StoredFloor(store)), Is.EqualTo((lostSlot + 1, First)), "the disk refused the sidecar and the floor");

        db.FailWrites = !diskRecovers;
        DataColumnSidecarPool restarted = Restart(store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.EarliestCompletelyServableSlot, Is.EqualTo(lostSlot + 1), "no slot at or below the one missing a column is servable; the ones above stay servable");
            Assert.That(StoredFloor(store), Is.EqualTo(diskRecovers ? lostSlot + 1 : First), "the raised floor is recorded where the disk takes it");
        }

        if (!diskRecovers)
        {
            db.FailWrites = false;
            restarted.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(Sampled[0], Last + 1, RootAt(Last + 1)));
            Assert.That(StoredFloor(store), Is.EqualTo(lostSlot + 1), "a floor the disk refused at start-up is recorded after the next stored sidecar");
        }
    }

    [Test]
    public void A_restart_raises_the_floor_above_the_highest_incomplete_slot()
    {
        (_, BeaconChainStore store, _) = StoreRange(skip: [(First + 7, Sampled[0]), (First + 2, Sampled[1])]);

        Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(First + 8));
    }

    [Test]
    public void A_slot_index_entry_naming_a_column_whose_record_is_gone_is_not_servable()
    {
        (FaultyColumnsDb db, BeaconChainStore store, _) = StoreRange();
        RemoveRecord(db, RootAt(First + 5), Sampled[2]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetStoredDataColumns(First + 5, RootAt(First + 5)) >> (int)Sampled[2] & UInt128.One, Is.EqualTo(UInt128.One), "the slot index still names it");
            Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(First + 6));
        }
    }

    [Test]
    public void Columns_missing_from_a_block_that_is_not_canonical_do_not_lower_servability()
    {
        (FaultyColumnsDb db, BeaconChainStore store, DataColumnSidecarPool pool) = StoreRange();
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(Sampled[0], First + 3, Keccak.Compute("competing")));
        store.ApplyCanonicalIndexChanges([(First + 6, null)], Last);
        RemoveRecord(db, RootAt(First + 6), Sampled[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetStoredDataColumns(First + 6, RootAt(First + 6)), Is.Not.EqualTo(UInt128.Zero), "the orphaned block's columns are still indexed");
            Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(First));
        }
    }

    [Test]
    public void A_canonical_block_with_no_stored_columns_needs_them_only_when_it_carries_blobs([Values] bool withBlobs)
    {
        (_, BeaconChainStore store, _) = StoreRange();
        const ulong slot = Last + 1;
        PutBlock(store, RootAt(slot), slot, withBlobs);
        store.ApplyCanonicalIndexChanges([(slot, RootAt(slot))], slot);

        Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(withBlobs ? slot + 1 : First));
    }

    [Test]
    public void A_canonical_block_that_is_not_stored_at_all_is_not_servable()
    {
        (_, BeaconChainStore store, _) = StoreRange();
        store.ApplyCanonicalIndexChanges([(Last + 1, RootAt(Last + 1))], Last + 1);

        Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(Last + 2));
    }

    [Test]
    public void A_complete_store_keeps_its_floor()
    {
        (_, BeaconChainStore store, _) = StoreRange();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(First));
            Assert.That(StoredFloor(store), Is.EqualTo(First));
        }
    }

    [Test]
    public void An_empty_store_claims_nothing_servable()
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        store.PutMetadata(BeaconDiscovery.IdentityMetadataKey, TestItem.PrivateKeyA.KeyBytes);
        store.ApplyCanonicalIndexChanges([(Last, RootAt(Last))], Last);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(ulong.MaxValue));
            Assert.That(store.TryGetDataColumnFloor(out _), Is.False, "nothing is recorded before the first sidecar");
        }
    }

    [Test]
    public void A_floor_above_every_stored_slot_is_left_alone()
    {
        (_, BeaconChainStore store, _) = StoreRange(skip: (First + 4, Sampled[0]));
        store.RaiseDataColumnFloor(Last + 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(Last + 5));
            Assert.That(StoredFloor(store), Is.EqualTo(Last + 5));
        }
    }

    [Test]
    public void Without_a_stored_identity_no_stored_block_is_claimed_servable()
    {
        (_, BeaconChainStore store, _) = StoreRange(withIdentity: false);

        Assert.That(Restart(store).EarliestCompletelyServableSlot, Is.EqualTo(Last + 1), "the columns the node was obliged to hold are unknown");
    }

    [Test]
    public void Slots_below_the_retention_window_are_not_checked()
    {
        (_, BeaconChainStore store, _) = StoreRange(skip: (First + 1, Sampled[0]));
        ulong windowStart = First + 5;
        ulong currentSlot = (windowStart / Spec.SlotsPerEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests) * Spec.SlotsPerEpoch;
        Assume.That(DataAvailabilityBoundary.ComputeStartSlot(Spec.GetEpoch(currentSlot), Spec), Is.EqualTo(windowStart));
        SlotClock clock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + currentSlot * Spec.SecondsPerSlot)));

        Assert.That(Restart(store, clock).EarliestCompletelyServableSlot, Is.EqualTo(First), "the prune raises the floor past that slot; the start-up check does not");
    }
}
