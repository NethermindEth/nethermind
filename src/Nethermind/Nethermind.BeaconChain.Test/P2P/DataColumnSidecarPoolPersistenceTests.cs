// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

// fulu/p2p-interface.md: a node MUST serve DataColumnSidecarsByRange/ByRoot for MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS,
// so a verified column must outlive the bounded in-memory pool and the process.
public class DataColumnSidecarPoolPersistenceTests
{
    private const ulong CurrentSlot = 13_410_304;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private static SlotClock ClockAt(ulong slot, ManualTimestamper? timestamper = null) =>
        new(Spec, timestamper ?? new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot)));

    private static (MemColumnsDb<BeaconChainDbColumns> Db, BeaconChainStore Store) CreateStore()
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        return (db, new BeaconChainStore(db, Spec));
    }

    private static Hash256 RootOf(DataColumnSidecar sidecar) => SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!);

    [Test]
    [CancelAfter(120_000)]
    public async Task Added_columns_are_served_by_root_and_by_range_after_a_restart_or_an_eviction([Values] bool restart)
    {
        (_, BeaconChainStore store) = CreateStore();
        SlotClock clock = ClockAt(CurrentSlot);
        DataColumnSidecar first = DataColumnSidecarTestFixture.BuildValidSidecar(5, CurrentSlot - 3);
        DataColumnSidecar second = DataColumnSidecarTestFixture.BuildValidSidecar(6, CurrentSlot - 2);
        store.SetCanonicalRoot(CurrentSlot - 3, RootOf(first));
        store.SetCanonicalRoot(CurrentSlot - 2, RootOf(second));
        DataColumnSidecarPool pool = new(capacity: restart ? 1 << 14 : 1, store, clock);
        pool.Add(RootOf(first), CurrentSlot - 3, first);
        pool.Add(RootOf(second), CurrentSlot - 2, second);
        if (restart)
        {
            pool = new DataColumnSidecarPool(store: store, clock: clock);
        }

        System.Collections.Generic.IReadOnlyList<DataColumnSidecar> served =
            await DataColumnSidecarsReqRespTests.RequestRangeAsync(pool, store, CurrentSlot - 3, 2, [5, 6], clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGet(RootOf(first), 5, out _), Is.True, "by root, the evicted or pre-restart column");
            Assert.That(pool.TryGet(RootOf(second), 6, out _), Is.True);
            Assert.That(served.Select(static s => s.Index), Is.EqualTo(new ulong[] { 5, 6 }), "by range, from the earliest slot the store holds completely");
        }
    }

    /// <summary>
    /// The first column this process is given can sit below the head it resumes from; it must not make the slots
    /// between it and the head look completely held, and a first column above the head leaves the slots in between unclaimed too.
    /// </summary>
    [Test]
    public void After_a_restart_a_column_below_the_stored_head_does_not_make_earlier_slots_servable([Values] bool withStore, [Values(-5, 3)] int firstSlotOffset)
    {
        const ulong top = CurrentSlot - 50;
        (_, BeaconChainStore store) = CreateStore();
        store.ApplyCanonicalIndexChanges([], top);
        DataColumnSidecarPool pool = new(store: withStore ? store : null);
        pool.SeedCompletelyServableFloor(store.GetCanonicalIndexTopSlot());
        ulong slot = (ulong)((long)top + firstSlotOffset);

        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, slot, Keccak.Compute("late")));

        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Math.Max(top + 1, slot)));
    }

    [Test]
    public void A_floor_the_store_recorded_survives_a_restart_and_is_not_replaced_by_the_seed()
    {
        const ulong stored = CurrentSlot - 1000;
        (_, BeaconChainStore store) = CreateStore();
        new DataColumnSidecarPool(store: store).AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, stored, Keccak.Compute("held")));

        DataColumnSidecarPool restarted = new(store: store);
        restarted.SeedCompletelyServableFloor(CurrentSlot - 10).Wait();
        restarted.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(1, stored - 500, Keccak.Compute("late")));

        Assert.That(restarted.EarliestCompletelyServableSlot, Is.EqualTo(stored), "the slots the store holds stay servable, and a late lower column never lowers the floor");
    }

    [Test]
    public void A_column_the_store_refused_is_not_claimed_servable_once_memory_evicts_it()
    {
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecarPool pool = new(store: store);
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot - 20, Keccak.Compute("held")));
        DataColumnSidecarGloas unstorable = DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot - 10, Keccak.Compute("unstorable"));
        unstorable.Index = Eip7594DasConstants.NumberOfColumns;

        pool.AddGloas(unstorable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(CurrentSlot - 9));
            Assert.That(new DataColumnSidecarPool(store: store).EarliestCompletelyServableSlot, Is.EqualTo(CurrentSlot - 9), "a restart must not claim the slot of the lost column complete again");
        }
    }

    [Test]
    public void A_floor_the_store_could_not_take_is_recorded_after_the_next_sidecar_is_stored()
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        DataColumnSidecarPool pool = new(store: store);
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot - 20, Keccak.Compute("held")));

        db.FailWrites = true;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot - 10, Keccak.Compute("lost")));
        db.FailWrites = false;
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot - 5, Keccak.Compute("later")));

        Assert.That(new DataColumnSidecarPool(store: store).EarliestCompletelyServableSlot, Is.EqualTo(CurrentSlot - 9));
    }

    [Test]
    public void A_late_column_below_the_stored_head_does_not_make_earlier_slots_servable_after_a_second_restart()
    {
        const ulong top = CurrentSlot - 50;
        (_, BeaconChainStore store) = CreateStore();
        store.ApplyCanonicalIndexChanges([], top);
        DataColumnSidecarPool first = new(store: store);
        first.SeedCompletelyServableFloor(store.GetCanonicalIndexTopSlot());
        first.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, top - 5, Keccak.Compute("late")));

        DataColumnSidecarPool second = new(store: store);
        second.SeedCompletelyServableFloor(store.GetCanonicalIndexTopSlot()).Wait();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetDataColumnFloor(out ulong stored), Is.True);
            Assert.That(stored, Is.EqualTo(top + 1), "the recorded floor already carries the seed, so no later start takes the late column's slot for it");
            Assert.That(second.EarliestCompletelyServableSlot, Is.EqualTo(top + 1));
        }
    }

    // fulu/p2p-interface.md: sidecars of a block that finalization left non-canonical are never requested, so they are dropped.
    [Test]
    public void Sidecars_of_a_block_finalization_left_non_canonical_are_dropped_as_columns_arrive()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot));
        (_, BeaconChainStore store) = CreateStore();
        ulong slot = CurrentSlot - 2000;
        Hash256 winner = Keccak.Compute("winner"), orphan = Keccak.Compute("orphan");
        store.ApplyCanonicalIndexChanges([(slot, winner)], slot);
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(0, slot, winner));
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(0, slot, orphan));
        BeaconChainStatusHolder status = new(Spec, timestamper)
        {
            CurrentStatus = new StatusMessageV2 { FinalizedEpoch = Spec.GetEpoch(slot) + 1, FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero },
        };
        DataColumnSidecarPool pool = new(store: store, clock: ClockAt(CurrentSlot, timestamper), status: status);

        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot, Keccak.Compute("new")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetDataColumnSidecarGloas(orphan, 0, out _), Is.False);
            Assert.That(store.TryGetDataColumnSidecarGloas(winner, 0, out _), Is.True);
        }
    }

    [Test]
    public void A_failing_store_read_is_not_held_instead_of_an_exception_and_serves_again_once_it_recovers([Values] bool gloas)
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        Hash256 root = Keccak.Compute("stored");
        DataColumnSidecar fulu = DataColumnSidecarTestFixture.BuildValidSidecar(4, CurrentSlot);
        root = gloas ? root : RootOf(fulu);
        if (gloas)
        {
            store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(4, CurrentSlot, root));
        }
        else
        {
            store.PutDataColumnSidecar(root, CurrentSlot, fulu);
        }

        DataColumnSidecarPool pool = new(store: store);
        db.FailReads = true;
        bool heldWhileFailing = true, reconstructable = true;
        Assert.DoesNotThrow(() =>
        {
            heldWhileFailing = gloas ? pool.TryGetGloas(root, 4, out _) : pool.TryGet(root, 4, out _);
            reconstructable = pool.TryGetHeldColumns(root, CurrentSlot, 1, out _);
        });
        db.FailReads = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldWhileFailing, Is.False);
            Assert.That(reconstructable, Is.False);
            Assert.That(gloas ? pool.TryGetGloas(root, 4, out _) : pool.TryGet(root, 4, out _), Is.True, "a transient fault must not mark the record unreadable");
        }
    }

    // A block imported after a restart waits for its columns; one the previous run stored must not keep it waiting for a wake that never comes.
    [Test]
    public void A_watch_counts_a_column_only_the_store_holds([Values] bool gloas)
    {
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecar fulu0 = DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot), fulu1 = DataColumnSidecarTestFixture.BuildValidSidecar(1, CurrentSlot);
        Hash256 root = gloas ? Keccak.Compute("block") : RootOf(fulu0);
        if (gloas)
        {
            store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot, root));
        }
        else
        {
            store.PutDataColumnSidecar(root, CurrentSlot, fulu0);
        }

        DataColumnSidecarPool restarted = new(store: store);
        bool woke = false;
        bool allStoredWatching = restarted.TryWatch(root, [0], gloas, () => { });
        bool watching = restarted.TryWatch(root, [0, 1], gloas, () => woke = true);
        if (gloas)
        {
            restarted.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(1, CurrentSlot, root));
        }
        else
        {
            restarted.Add(root, CurrentSlot, fulu1);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(watching, Is.True, "column 1 is missing");
            Assert.That(woke, Is.True, "the last missing column arrived");
            Assert.That(allStoredWatching, Is.False, "every column asked for is held already");
        }
    }

    // The importer wakes on the callback and reads the store, so the column must be durable before it runs.
    [Test]
    public void A_column_is_stored_before_the_watcher_is_woken([Values] bool gloas)
    {
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecar fulu = DataColumnSidecarTestFixture.BuildValidSidecar(2, CurrentSlot);
        Hash256 root = gloas ? Keccak.Compute("block") : RootOf(fulu);
        DataColumnSidecarPool pool = new(store: store);
        bool storedWhenWoken = false;
        pool.TryWatch(root, [2], gloas, () => storedWhenWoken = store.HasDataColumnRecord(root, 2));

        if (gloas)
        {
            pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(2, CurrentSlot, root));
        }
        else
        {
            pool.Add(root, CurrentSlot, fulu);
        }

        Assert.That(storedWhenWoken, Is.True);
    }

    // das-core.md: a node holding at least half the columns SHOULD reconstruct, and a restart must not forget the ones it stored.
    [Test]
    public void Columns_stored_before_a_restart_count_toward_the_held_columns()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecarPool before = new(store: store);
        Hash256 root = RootOf(DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        for (ulong column = 0; column < required - 1; column++)
        {
            before.Add(root, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot));
        }

        DataColumnSidecarPool restarted = new(store: store);
        restarted.Add(root, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(required - 1, CurrentSlot));

        bool held = restarted.TryGetHeldColumns(root, CurrentSlot, required, out DataColumnSidecar[]? columns);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.True);
            Assert.That(columns!.Select(static c => c.Index), Is.EqualTo(Enumerable.Range(0, required).Select(static i => (ulong)i)));
            Assert.That(restarted.TryGetHeldColumns(root, CurrentSlot, required + 1, out _), Is.False);
        }
    }

    // A slot index that names a column whose record is gone must not count that column as held.
    [Test]
    public void A_column_the_slot_index_names_without_a_record_is_not_counted_as_held()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = CreateStore();
        Hash256 root = RootOf(DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        for (ulong column = 0; column < required; column++)
        {
            store.PutDataColumnSidecar(root, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot));
        }

        db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).Remove([.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 3]);
        DataColumnSidecarPool pool = new(store: store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGetHeldColumns(root, CurrentSlot, required, out _), Is.False);
            Assert.That(pool.TryGetHeldColumns(root, CurrentSlot, required - 1, out DataColumnSidecar[]? held), Is.True);
            Assert.That(held!.Select(static c => c.Index), Is.EqualTo(Enumerable.Range(0, required).Where(static i => i != 3).Select(static i => (ulong)i)));
        }
    }

    [Test]
    public void Stored_sidecars_below_the_window_are_pruned_once_per_epoch_as_columns_arrive()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot));
        SlotClock clock = ClockAt(CurrentSlot, timestamper);
        (_, BeaconChainStore store) = CreateStore();
        ulong edge = DataAvailabilityBoundary.ComputeStartSlot(clock.CurrentEpoch, Spec);
        DataColumnSidecarPool pool = new(store: store, clock: clock);
        Hash256 old = Keccak.Compute("old"), oldToo = Keccak.Compute("old too");
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(0, edge - 1, old));

        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot, Keccak.Compute("new")));
        bool prunedOnFirstAdd = !store.TryGetDataColumnSidecarGloas(old, 0, out _);
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(0, edge - 1, oldToo));
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(1, CurrentSlot, Keccak.Compute("new")));
        bool keptWithinTheEpoch = store.TryGetDataColumnSidecarGloas(oldToo, 0, out _);
        timestamper.Add(TimeSpan.FromSeconds(Spec.SecondsPerSlot * Spec.SlotsPerEpoch));
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(2, CurrentSlot + Spec.SlotsPerEpoch, Keccak.Compute("newer")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prunedOnFirstAdd, Is.True, "a stored sidecar below the retention window is deleted");
            Assert.That(keptWithinTheEpoch, Is.True, "the store is pruned once per epoch, not on every column");
            Assert.That(store.TryGetDataColumnSidecarGloas(oldToo, 0, out _), Is.False, "and again when the epoch changes");
        }
    }

    [Test]
    public void A_damaged_stored_sidecar_is_not_served_until_it_is_added_again()
    {
        (MemColumnsDb<BeaconChainDbColumns> db, BeaconChainStore store) = CreateStore();
        Hash256 root = Keccak.Compute("damaged");
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(3, CurrentSlot, root));
        db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).Set([.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 3], [1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3]);
        DataColumnSidecarPool pool = new(capacity: 1, store);

        bool damaged = pool.TryGetGloas(root, 3, out _);
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, CurrentSlot, root));
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot + 1, Keccak.Compute("evicts")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(damaged, Is.False);
            Assert.That(pool.TryGetGloas(root, 3, out _), Is.True, "read from the store once memory evicted it, so the unreadable marker must be gone");
        }
    }

    // A damaged record replaced while a reader decodes it must not stay refused once memory evicts the replacement.
    [Test]
    public void A_record_repaired_while_it_is_read_is_served_after_memory_evicts_it()
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        Hash256 root = Keccak.Compute("repaired");
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(3, CurrentSlot, root));
        db.GetColumnDb(BeaconChainDbColumns.DataColumnSidecars).Set([.. root.Bytes, 0, 0, 0, 0, 0, 0, 0, 3], [1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3]);
        DataColumnSidecarPool pool = new(capacity: 1, store);
        db.AfterNextRecordRead = () => pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, CurrentSlot, root));

        bool servedWhileDamaged = pool.TryGetGloas(root, 3, out _);
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, CurrentSlot + 1, Keccak.Compute("evicts")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedWhileDamaged, Is.False);
            Assert.That(pool.TryGetGloas(root, 3, out _), Is.True);
        }
    }

    [Test]
    public void Pool_from_the_module_reads_the_store_the_module_registers()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        Hash256 root = Keccak.Compute("stored");
        store.PutDataColumnSidecar(DataColumnSidecarGloasTestFixture.BuildSidecar(7, CurrentSlot, root));

        Assert.That(container.Resolve<DataColumnSidecarPool>().TryGetGloas(root, 7, out _), Is.True);
    }

    [Test]
    public void The_service_seeds_the_floor_from_the_stored_head_before_any_column_is_given()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        ulong top = container.Resolve<SlotClock>().CurrentSlot - 50;
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        DataColumnSidecarPool pool = container.Resolve<DataColumnSidecarPool>();
        store.SetAnchor(TestItem.KeccakA, top - 10);
        store.ApplyCanonicalIndexChanges([], top);

        // The anchor state is missing, so the start step stops after it has seeded the pool.
        Assert.Throws<InvalidOperationException>(() => container.Resolve<BeaconChainService>().Start());
        pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(0, top - 5, Keccak.Compute("late")));
        DrainStoreWrites(container.Resolve<ColumnStoreWriter>());

        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(top + 1));
    }

    /// <summary>
    /// Gossip validation runs inside the pubsub router's lock, which every peer's read loop waits for on a thread-pool thread, so adding a
    /// verified column must not wait for the store: the write runs later on the writer's own thread, and memory serves the column until then.
    /// </summary>
    [Test]
    public void Adding_a_column_returns_before_its_store_write_which_runs_off_the_thread_pool([Values] bool gloas, [Values] bool fromModule)
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        using ColumnStoreWriter ownWriter = new(LimboLogs.Instance);
        SlotClock clock = container.Resolve<SlotClock>();
        ulong slot = clock.CurrentSlot - 1;
        (BeaconChainStore store, DataColumnSidecarPool pool, ColumnStoreWriter writer) = fromModule
            ? (container.Resolve<BeaconChainStore>(), container.Resolve<DataColumnSidecarPool>(), container.Resolve<ColumnStoreWriter>())
            : WithWriter(ownWriter);
        using ManualResetEventSlim release = new();
        writer.Post(() => release.Wait(TimeSpan.FromSeconds(30)));
        Hash256 root;
        if (gloas)
        {
            root = Keccak.Compute("written later");
            pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(7, slot, root));
        }
        else
        {
            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(7, slot);
            root = RootOf(sidecar);
            pool.Add(root, slot, sidecar);
        }

        bool storedWhileWriterBusy = Stored(store, root, gloas);
        bool servedWhileWriterBusy = gloas ? pool.TryGetGloas(root, 7, out _) : pool.TryGet(root, 7, out _);
        release.Set();
        bool? writerOnPoolThread = null;
        writer.Post(() => writerOnPoolThread = Thread.CurrentThread.IsThreadPoolThread);
        DrainStoreWrites(writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedWhileWriterBusy, Is.False, "the caller wrote the store");
            Assert.That(servedWhileWriterBusy, Is.True, "memory serves the column before the write");
            Assert.That(Stored(store, root, gloas), Is.True, "the queued write was lost");
            Assert.That(writerOnPoolThread, Is.False);
        }

        (BeaconChainStore, DataColumnSidecarPool, ColumnStoreWriter) WithWriter(ColumnStoreWriter own)
        {
            (_, BeaconChainStore created) = CreateStore();
            return (created, new DataColumnSidecarPool(store: created, clock: clock, storeWriter: own), own);
        }

        static bool Stored(BeaconChainStore store, Hash256 root, bool gloas) =>
            gloas ? store.TryGetDataColumnSidecarGloas(root, 7, out _) : store.TryGetDataColumnSidecar(root, 7, out _);
    }

    /// <summary>A column memory evicts while its store write is still queued must stay readable, as the slot is already claimed held.</summary>
    [Test]
    public void A_column_evicted_from_memory_before_its_store_write_is_still_served([Values] bool gloas)
    {
        using ColumnStoreWriter writer = new(LimboLogs.Instance);
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecarPool pool = new(capacity: 1, store: store, clock: ClockAt(CurrentSlot), storeWriter: writer);
        using ManualResetEventSlim release = new();
        writer.Post(() => release.Wait(TimeSpan.FromSeconds(30)));
        Hash256 evicted = Add(5, CurrentSlot - 3);
        Add(6, CurrentSlot - 2);

        bool servedWhileQueued = gloas ? pool.TryGetGloas(evicted, 5, out _) : pool.TryGet(evicted, 5, out _);
        bool watchedWhileQueued = pool.TryWatch(evicted, [5], gloas, static () => { });
        release.Set();
        DrainStoreWrites(writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(servedWhileQueued, Is.True);
            Assert.That(watchedWhileQueued, Is.False, "an import waiting for the column would wait for a wake that never comes");
            Assert.That(gloas ? pool.TryGetGloas(evicted, 5, out _) : pool.TryGet(evicted, 5, out _), Is.True, "served from the store once written");
        }

        Hash256 Add(ulong column, ulong slot)
        {
            if (gloas)
            {
                Hash256 root = Keccak.Compute($"block at {slot}");
                pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, slot, root));
                return root;
            }

            DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(column, slot);
            pool.Add(RootOf(sidecar), slot, sidecar);
            return RootOf(sidecar);
        }
    }

    // das-core.md: a node holding at least half the columns SHOULD reconstruct, so a column waiting for its store write still counts.
    [Test]
    public void Columns_evicted_from_memory_before_their_store_write_count_toward_the_held_columns()
    {
        const int required = Eip7594DasConstants.RequiredColumnsForReconstruction;
        using ColumnStoreWriter writer = new(LimboLogs.Instance);
        (_, BeaconChainStore store) = CreateStore();
        DataColumnSidecarPool pool = new(capacity: required / 2, store: store, storeWriter: writer);
        using ManualResetEventSlim release = new();
        writer.Post(() => release.Wait(TimeSpan.FromSeconds(30)));
        Hash256 root = RootOf(DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        for (ulong column = 0; column < required; column++)
        {
            pool.Add(root, CurrentSlot, DataColumnSidecarTestFixture.BuildValidSidecar(column, CurrentSlot));
        }

        bool held = pool.TryGetHeldColumns(root, CurrentSlot, required, out DataColumnSidecar[]? columns);
        release.Set();
        DrainStoreWrites(writer);

        Assert.That(held ? columns!.Select(static c => c.Index) : [], Is.EqualTo(Enumerable.Range(0, required).Select(static i => (ulong)i)));
    }

    internal static void DrainStoreWrites(ColumnStoreWriter writer)
    {
        using ManualResetEventSlim drained = new();
        writer.Post(drained.Set);
        Assert.That(drained.Wait(TimeSpan.FromSeconds(30)), Is.True, "the queued store writes did not finish");
    }
}
