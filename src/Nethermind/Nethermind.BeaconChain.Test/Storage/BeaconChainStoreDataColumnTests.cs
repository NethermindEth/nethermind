// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Storage;

// fulu/p2p-interface.md DataColumnSidecarsByRange/ByRoot: a node MUST serve columns for MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS,
// so a verified column must outlive the in-memory pool and the process, and must not outlive the window.
public class BeaconChainStoreDataColumnTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly byte[] BoundsKey = [0];
    private static readonly ulong FirstSlot = Spec.FuluForkEpoch * Spec.SlotsPerEpoch + 1_000;

    private static (MemColumnsDb<BeaconChainDbColumns> Db, BeaconChainStore Store) Create()
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        return (db, new BeaconChainStore(db, Spec));
    }

    private static Hash256 Root(string name) => Keccak.Compute(name);

    private static void Put(BeaconChainStore store, Hash256 root, ulong slot, ulong column = 0) =>
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(column, slot, root));

    private static bool Holds(BeaconChainStore store, Hash256 root, ulong column = 0) =>
        store.TryGetDataColumnSidecarGloas(root, column, out _);

    [Test]
    public void A_stored_sidecar_reads_back_in_its_own_shape_only([Values] bool fulu)
    {
        (_, BeaconChainStore store) = Create();
        Hash256 root = Root("block");
        DataColumnSidecar fuluSidecar = DataColumnSidecarTestFixture.BuildValidSidecar(5, FirstSlot);
        if (fulu)
        {
            store.PutDataColumnSidecar(root, FirstSlot, fuluSidecar);
        }
        else
        {
            Put(store, root, FirstSlot, 5);
        }

        bool readAsFulu = store.TryGetDataColumnSidecar(root, 5, out DataColumnSidecar? readFulu);
        bool readAsGloas = store.TryGetDataColumnSidecarGloas(root, 5, out DataColumnSidecarGloas? readGloas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readAsFulu, Is.EqualTo(fulu));
            Assert.That(readAsGloas, Is.EqualTo(!fulu));
            Assert.That(fulu ? DataColumnSidecar.Encode(readFulu!) : DataColumnSidecarGloas.Encode(readGloas!),
                Is.EqualTo(fulu ? DataColumnSidecar.Encode(fuluSidecar) : DataColumnSidecarGloas.Encode(DataColumnSidecarGloasTestFixture.BuildSidecar(5, FirstSlot, root))));
            Assert.That(store.TryGetDataColumnSidecar(Root("other"), 5, out _), Is.False);
        }
    }

    [Test]
    public void A_damaged_record_is_reported_instead_of_returned([Values] bool truncated)
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        Hash256 root = Root("block");
        Put(store, root, FirstSlot, 3);
        IDb table = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
        byte[] key = [.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 3];
        byte[] record = table.Get(key)!;
        table.Set(key, truncated ? record[..20] : [.. record[..9], .. record[9..].Select(static b => (byte)~b)]);

        Assert.Throws<InvalidDataException>(() => store.TryGetDataColumnSidecarGloas(root, 3, out _));
    }

    // A record served under another column's index would answer a request with a column the peer did not ask for.
    [Test]
    public void A_record_filed_under_another_column_index_is_reported_instead_of_returned([Values] bool fulu)
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        Hash256 root = Root("block");
        if (fulu)
        {
            store.PutDataColumnSidecar(root, FirstSlot, DataColumnSidecarTestFixture.BuildValidSidecar(3, FirstSlot));
        }
        else
        {
            Put(store, root, FirstSlot, 3);
        }

        IDb table = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
        table.Set([.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 4], table.Get([.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 3])!);

        Assert.Throws<InvalidDataException>(() =>
        {
            if (fulu)
            {
                store.TryGetDataColumnSidecar(root, 4, out _);
            }
            else
            {
                store.TryGetDataColumnSidecarGloas(root, 4, out _);
            }
        });
    }

    [Test]
    public void The_stored_column_bitmap_names_the_columns_of_one_root_at_one_slot()
    {
        (_, BeaconChainStore store) = Create();
        Hash256 root = Root("block"), sibling = Root("sibling");
        Put(store, root, FirstSlot, 0);
        Put(store, root, FirstSlot, 127);
        Put(store, sibling, FirstSlot, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetStoredDataColumns(FirstSlot, root), Is.EqualTo(UInt128.One | UInt128.One << 127));
            Assert.That(store.GetStoredDataColumns(FirstSlot, sibling), Is.EqualTo(UInt128.One << 5));
            Assert.That(store.GetStoredDataColumns(FirstSlot + 1, root), Is.EqualTo(UInt128.Zero));
            Assert.That(store.HasDataColumnRecord(root, 127), Is.True);
            Assert.That(store.HasDataColumnRecord(root, 5), Is.False);
            Assert.That(store.HasDataColumnRecord(root, Eip7594DasConstants.NumberOfColumns), Is.False);
        }
    }

    // A prune runs on the thread that added a column, so one call must not be held for a whole backlog after a long outage.
    [Test]
    public void One_prune_call_deletes_a_bounded_backlog_and_the_next_call_finishes_it([Values] bool restart)
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 100;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Hash256 oldest = Root("oldest"), newest = Root("newest");
        Put(store, oldest, edge - 1 - (BeaconChainStore.MaxColumnPruneBatchesPerCall + 1) * BeaconChainStore.ColumnPruneBatchSlots);
        Put(store, newest, edge - 1);

        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);
        bool oldestGoneAfterOne = !Holds(store, oldest), newestKeptAfterOne = Holds(store, newest);
        store = restart ? new BeaconChainStore(db, Spec) : store;
        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldestGoneAfterOne, Is.True);
            Assert.That(newestKeptAfterOne, Is.True, "the call stops after its batch budget and leaves the rest for the next one");
            Assert.That(Holds(store, newest), Is.False);
        }
    }

    [Test]
    public void One_non_canonical_pass_checks_a_bounded_range_and_the_next_call_finishes_it([Values] bool restart)
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + 10;
        ulong first = FirstSlot, last = first + (BeaconChainStore.MaxColumnPruneBatchesPerCall + 1) * BeaconChainStore.ColumnPruneBatchSlots;
        Hash256 canonical = Root("canonical"), earlyOrphan = Root("early orphan"), lateOrphan = Root("late orphan");
        store.ApplyCanonicalIndexChanges([(first, canonical), (last, canonical)], last);
        Put(store, earlyOrphan, first);
        Put(store, lateOrphan, last);

        store.PruneDataColumnSidecars(currentEpoch, last + 1);
        bool earlyGoneAfterOne = !Holds(store, earlyOrphan), lateKeptAfterOne = Holds(store, lateOrphan);
        store = restart ? new BeaconChainStore(db, Spec) : store;
        store.PruneDataColumnSidecars(currentEpoch, last + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(earlyGoneAfterOne, Is.True);
            Assert.That(lateKeptAfterOne, Is.True);
            Assert.That(Holds(store, lateOrphan), Is.False);
        }
    }

    // A block that reaches the canonical index after a pass must still have its competing sidecars dropped once it does.
    [Test]
    public void A_slot_whose_block_is_indexed_after_a_pass_is_checked_again()
    {
        (_, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + 10;
        ulong slot = FirstSlot + 5, finalized = FirstSlot + 40;
        Hash256 winner = Root("winner"), orphan = Root("orphan");
        store.ApplyCanonicalIndexChanges([], slot - 1);
        Put(store, winner, slot);
        Put(store, orphan, slot);

        store.PruneDataColumnSidecars(currentEpoch, finalized);
        bool keptWhileUnindexed = Holds(store, orphan);
        store.ApplyCanonicalIndexChanges([(slot, winner)], slot);
        store.PruneDataColumnSidecars(currentEpoch, finalized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keptWhileUnindexed, Is.True, "nothing shows yet which block wins the slot");
            Assert.That(Holds(store, orphan), Is.False);
            Assert.That(Holds(store, winner), Is.True);
        }
    }

    // A sidecar verified before finalization can be stored after a pass has already checked its slot.
    [Test]
    public void A_sidecar_stored_below_a_checked_slot_is_checked_on_the_next_pass()
    {
        (_, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + 10;
        ulong slot = FirstSlot + 5, finalized = FirstSlot + 40;
        Hash256 winner = Root("winner"), lateOrphan = Root("late orphan");
        store.ApplyCanonicalIndexChanges([(slot, winner)], finalized);
        Put(store, winner, slot);
        Put(store, winner, finalized - 1);
        store.PruneDataColumnSidecars(currentEpoch, finalized);

        Put(store, lateOrphan, slot);
        store.PruneDataColumnSidecars(currentEpoch, finalized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(store, lateOrphan), Is.False);
            Assert.That(Holds(store, winner), Is.True);
        }
    }

    [Test]
    public void A_slot_below_the_canonical_top_with_no_indexed_block_does_not_hold_back_later_slots()
    {
        (_, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + 10;
        ulong skipped = FirstSlot + 5, later = skipped + BeaconChainStore.ColumnPruneBatchSlots + 1, finalized = later + 10;
        Hash256 winner = Root("winner"), orphan = Root("orphan");
        store.ApplyCanonicalIndexChanges([(later, winner)], later);
        Put(store, Root("skipped slot"), skipped);
        Put(store, winner, later);
        Put(store, orphan, later);

        store.PruneDataColumnSidecars(currentEpoch, finalized);

        Assert.That(Holds(store, orphan), Is.False, "an empty slot below the top is settled, so the cursor moves past it");
    }

    /// <summary>The window is <c>max(current_epoch - MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS, FULU_FORK_EPOCH)</c> (fulu/p2p-interface.md).</summary>
    [Test]
    public void Pruning_removes_the_slot_below_the_window_and_keeps_the_window_start()
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Hash256 below = Root("below"), atEdge = Root("edge"), newer = Root("newer");
        Put(store, below, edge - 1, 0);
        Put(store, below, edge - 1, 127);
        Put(store, atEdge, edge, 0);
        Put(store, newer, edge + 100, 1);

        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(store, below, 0), Is.False);
            Assert.That(Holds(store, below, 127), Is.False);
            Assert.That(Holds(store, atEdge), Is.True, "the first slot of the window is still served");
            Assert.That(Holds(store, newer, 1), Is.True);
            Assert.That(db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).GetAllKeys().Count(), Is.EqualTo(2 + 2 + 1), "two records and their slot entries survive, plus the bounds record");
        }
    }

    [Test]
    public void Pruning_more_slots_than_one_batch_deletes_all_of_them_and_leaves_no_index_behind()
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 100;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        ulong first = edge - 2 * BeaconChainStore.ColumnPruneBatchSlots - 5;
        for (ulong slot = first; slot < edge; slot += 7)
        {
            Put(store, Root($"old {slot}"), slot);
        }

        Hash256 kept = Root("kept");
        Put(store, kept, edge + 3);

        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        IDb table = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(store, Root($"old {first}")), Is.False);
            Assert.That(Holds(store, kept), Is.True);
            Assert.That(table.GetAllKeys().Count(), Is.EqualTo(1 + 1 + 1), "the kept record, its slot entry and the bounds record");
        }
    }

    [Test]
    public void Non_canonical_sidecars_below_the_finalized_slot_are_dropped_and_the_rest_kept()
    {
        (_, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + 10;
        ulong finalized = FirstSlot + 40;
        Hash256 canonical = Root("canonical"), orphan = Root("orphan"), unknown = Root("unknown"), aboveFinalized = Root("above");
        store.SetCanonicalRoot(FirstSlot + 1, canonical);
        Put(store, canonical, FirstSlot + 1, 4);
        Put(store, orphan, FirstSlot + 1, 4);
        Put(store, orphan, FirstSlot + 1, 9);
        Put(store, unknown, FirstSlot + 2);
        store.SetCanonicalRoot(finalized + 1, Root("other"));
        Put(store, aboveFinalized, finalized + 1);

        store.PruneDataColumnSidecars(currentEpoch, finalized);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Holds(store, canonical, 4), Is.True);
            Assert.That(Holds(store, orphan, 4), Is.False, "a block that lost its slot to the canonical one can never become canonical after finalization");
            Assert.That(Holds(store, orphan, 9), Is.False);
            Assert.That(Holds(store, unknown), Is.True, "no canonical entry yet, so nothing shows it is orphaned");
            Assert.That(Holds(store, aboveFinalized), Is.True, "not final yet");
        }
    }

    [Test]
    public void A_root_stored_again_under_another_slot_is_pruned_with_its_new_slot_only()
    {
        (_, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Hash256 root = Root("moved");
        Put(store, root, edge - 5);
        Put(store, root, edge + 5);

        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        Assert.That(Holds(store, root), Is.True, "the old slot's index entry must not delete the record now kept under the new slot");
    }

    [Test]
    public void The_floor_only_rises_and_pruning_raises_it_to_the_window_start()
    {
        (_, BeaconChainStore store) = Create();
        Assert.That(store.TryGetDataColumnFloor(out _), Is.False);
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);

        store.RaiseDataColumnFloor(edge - 100);
        store.RaiseDataColumnFloor(edge - 200);
        store.TryGetDataColumnFloor(out ulong afterLower);
        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);
        store.TryGetDataColumnFloor(out ulong afterPrune);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLower, Is.EqualTo(edge - 100));
            Assert.That(afterPrune, Is.EqualTo(edge), "slots below the window are no longer held, so they cannot count as complete");
        }
    }

    [Test]
    public void A_damaged_bounds_record_is_rebuilt_so_old_sidecars_are_still_pruned([Values("short", "inverted")] string damage)
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Hash256 old = Root("old");
        Put(store, old, edge - 3);
        byte[] bounds = new byte[16];
        if (damage == "inverted")
        {
            BinaryPrimitives.WriteUInt64BigEndian(bounds, edge);
            BinaryPrimitives.WriteUInt64BigEndian(bounds.AsSpan(8), edge - 3);
        }
        else
        {
            bounds = [1, 2, 3];
        }

        db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).Set(BoundsKey, bounds);

        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        Assert.That(Holds(store, old), Is.False);
    }

    // A prune that stops part way must never leave the floor below a slot whose sidecars it already deleted.
    [Test]
    public void The_floor_reaches_the_window_start_even_when_the_deletes_fail()
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Put(store, Root("old"), edge - 3);
        store.RaiseDataColumnFloor(edge - 3);
        db.FailDeletes = true;

        Assert.Throws<InvalidOperationException>(() => store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0));

        Assert.That(store.TryGetDataColumnFloor(out ulong floor) ? floor : (ulong?)null, Is.EqualTo(edge));
    }

    // Damage that leaves the slot index out of step with the records must not crash a read or a prune.
    [Test]
    public void A_slot_index_entry_with_a_torn_tail_is_read_and_pruned_by_its_whole_entries()
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = Create();
        ulong currentEpoch = Spec.FuluForkEpoch + Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 10;
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(currentEpoch, Spec);
        Hash256 root = Root("old");
        Put(store, root, edge - 1, 7);
        IDb table = db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars);
        byte[] slotKey = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(slotKey, edge - 1);
        table.Set(slotKey, [.. table.Get(slotKey)!, 1, 2, 3]);

        UInt128 stored = store.GetStoredDataColumns(edge - 1, root);
        store.PruneDataColumnSidecars(currentEpoch, finalizedSlot: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.EqualTo(UInt128.One << 7));
            Assert.That(Holds(store, root, 7), Is.False);
            Assert.That(table.Get(slotKey), Is.Null);
        }
    }

    // An older database has an empty column table and no floor, so it is upgraded in place; a newer one is refused (see BeaconChainServiceStartupTests).
    [Test]
    public void A_database_stamped_by_the_previous_version_is_accepted_and_restamped()
    {
        (_, BeaconChainStore store) = Create();
        store.SetSchemaVersion(BeaconChainStore.CurrentSchemaVersion - 1);

        store.EnsureSchemaVersion();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BeaconChainStore.CurrentSchemaVersion, Is.EqualTo(4u));
            Assert.That(store.TryGetSchemaVersion(out uint version) ? version : 0, Is.EqualTo(4u));
            Assert.That(store.TryGetDataColumnFloor(out _), Is.False, "no floor, so the pool seeds one from the canonical index");
        }
    }

    [Test]
    public void A_column_index_outside_the_matrix_is_refused()
    {
        (_, BeaconChainStore store) = Create();

        DataColumnSidecarGloas sidecar = DataColumnSidecarGloasTestFixture.BuildSidecar(0, FirstSlot, Root("block"));
        sidecar.Index = Eip7594DasConstants.NumberOfColumns;

        Assert.Throws<ArgumentException>(() => store.PutDataColumnSidecar(sidecar));
    }
}
