// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Lmdb.Test;

public class LmdbDbTests
{
    private const long TestMapSize = 64L * 1024 * 1024;
    private const int TestMaxReaders = 8192;
    private const int TestMaxOperationsPerWave = 4096;

    private string DbPath => Path.Combine("testdb-lmdb", TestContext.CurrentContext.Test.ID);
    private LmdbDb _db = null!;

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
        _db = new LmdbDb("test", DbPath, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Get_set_delete_round_trip()
    {
        byte[] value = TestItem.KeccakA.BytesToArray();
        _db.Set(TestItem.KeccakA.Bytes, value);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(value));

        _db.Set(TestItem.KeccakA.Bytes, null);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.Null);
    }

    [Test]
    public void Get_missing_key_returns_null() => Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.Null);

    // LMDB's own Delete reports NotFound for a missing key; IWriteOnlyKeyValueStore.Set's contract says deleting
    // something already absent is a no-op, not an error - see LmdbDb.DeleteAllowingMissing.
    [Test]
    public void Delete_of_a_missing_key_is_not_an_error() =>
        Assert.That(() => _db.Set(TestItem.KeccakA.Bytes, null), Throws.Nothing);

    [Test]
    public void Set_overwrites_existing_value()
    {
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakB.BytesToArray());
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void Multi_get_returns_values_and_nulls_for_missing_keys()
    {
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        _db.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());

        KeyValuePair<byte[], byte[]?>[] result = _db[[TestItem.KeccakA.BytesToArray(), TestItem.KeccakC.BytesToArray(), TestItem.KeccakB.BytesToArray()]];

        Assert.That(result, Has.Length.EqualTo(3));
        Assert.That(result[0].Value, Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(result[1].Value, Is.Null);
        Assert.That(result[2].Value, Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void Set_with_DisableWAL_still_round_trips_correctly()
    {
        // DisableWAL selects the per-transaction NoSync path (see LmdbDb.CommitWaveWithMapGrowthRetry) - it must
        // not affect read-after-write correctness within the process, only whether the write survives a crash.
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray(), WriteFlags.DisableWAL);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void Write_batch_with_DisableWAL_still_round_trips_correctly()
    {
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            batch.PutSpan(TestItem.KeccakA.Bytes, TestItem.KeccakA.Bytes, WriteFlags.DisableWAL);
        }

        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void Write_batch_is_visible_only_after_dispose()
    {
        IWriteBatch batch = _db.StartWriteBatch();
        batch.PutSpan(TestItem.KeccakA.Bytes, TestItem.KeccakA.Bytes);

        // The write transaction is only committed on batch Dispose(); reads from outside the batch use their own
        // read-only transaction against the last committed root, so the pending write must not be visible yet.
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.Null);

        batch.Dispose();

        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void Write_batch_can_delete_within_the_batch()
    {
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());

        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            batch.Set(TestItem.KeccakA.Bytes, null);
            batch.PutSpan(TestItem.KeccakB.Bytes, TestItem.KeccakB.Bytes);
        }

        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.Null);
        Assert.That(_db.Get(TestItem.KeccakB.Bytes), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
    }

    [Test]
    public void Write_batch_merge_is_not_supported()
    {
        using IWriteBatch batch = _db.StartWriteBatch();
        Assert.That(() => batch.Merge(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray()), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void GetAll_enumerates_every_key_in_order()
    {
        _db.Set(TestItem.KeccakE.Bytes, [1]);
        _db.Set(TestItem.KeccakC.Bytes, [2]);
        _db.Set(TestItem.KeccakA.Bytes, [3]);
        _db.Set(TestItem.KeccakB.Bytes, [4]);
        _db.Set(TestItem.KeccakD.Bytes, [5]);

        byte[][] keys = [.. _db.GetAll(true).Select(kvp => kvp.Key)];
        Assert.That(keys, Has.Length.EqualTo(5));
        for (int i = 0; i < keys.Length - 1; i++)
        {
            Assert.That(Bytes.BytesComparer.Compare(keys[i], keys[i + 1]), Is.LessThan(0));
        }

        Assert.That(_db.GetAllKeys().Count(), Is.EqualTo(5));
        Assert.That(_db.GetAllValues().Count(), Is.EqualTo(5));
    }

    [Test]
    public void Dispose_and_reopen_preserves_data()
    {
        _db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        _db.Dispose();

        _db = new LmdbDb("test", DbPath, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, LimboLogs.Instance);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void FirstKey_and_LastKey_reflect_the_extremes()
    {
        _db.Set([1, 1, 1], [1]);
        _db.Set([2, 2, 2], [2]);
        _db.Set([0, 0, 0], [0]);

        Assert.That(_db.FirstKey, Is.EqualTo(new byte[] { 0, 0, 0 }));
        Assert.That(_db.LastKey, Is.EqualTo(new byte[] { 2, 2, 2 }));
    }

    [Test]
    public void FirstKey_and_LastKey_are_null_when_empty()
    {
        Assert.That(_db.FirstKey, Is.Null);
        Assert.That(_db.LastKey, Is.Null);
    }

    [Test]
    public void GetViewBetween_returns_the_half_open_range_in_order()
    {
        for (byte i = 0; i < 5; i++)
        {
            _db.Set([i, i, i], [i, i, i]);
        }

        using ISortedView view = _db.GetViewBetween([1, 0, 0], [4, 0, 0]);

        byte expected = 1;
        while (view.MoveNext())
        {
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { expected, expected, expected }));
            Assert.That(view.CurrentValue.ToArray(), Is.EqualTo(new byte[] { expected, expected, expected }));
            expected++;
        }

        // [1,0,0]..[4,0,0) covers keys 1..3 inclusive; key 4 is excluded (its bytes are [4,4,4] >= [4,0,0]).
        Assert.That(expected, Is.EqualTo((byte)4));
    }

    [Test]
    public void GetViewBetween_on_an_empty_db_yields_nothing()
    {
        using ISortedView view = _db.GetViewBetween([0], [255]);
        Assert.That(view.MoveNext(), Is.False);
    }

    [Test]
    public void StartBefore_positions_on_the_greatest_key_less_than_or_equal_to_the_seek_value()
    {
        _db.Set([1], [1]);
        _db.Set([3], [3]);
        _db.Set([5], [5]);

        using ISortedView view = _db.GetViewBetween([0], [10]);
        Assert.That(view.StartBefore([4]), Is.True);
        Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 3 }));

        Assert.That(view.MoveNext(), Is.True);
        Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(view.MoveNext(), Is.False);
    }

    [Test]
    public void StartBefore_on_an_exact_match_positions_on_that_key()
    {
        _db.Set([1], [1]);
        _db.Set([3], [3]);
        _db.Set([5], [5]);

        using ISortedView view = _db.GetViewBetween([0], [10]);
        Assert.That(view.StartBefore([3]), Is.True);
        Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 3 }));
    }

    [Test]
    public void StartBefore_when_the_seek_value_is_smaller_than_every_key_finds_nothing()
    {
        _db.Set([3], [3]);
        _db.Set([5], [5]);

        using ISortedView view = _db.GetViewBetween([0], [10]);
        Assert.That(view.StartBefore([1]), Is.False);
    }

    [Test]
    public void StartBefore_when_the_seek_value_is_past_every_key_positions_on_the_last_key()
    {
        _db.Set([1], [1]);
        _db.Set([3], [3]);

        using ISortedView view = _db.GetViewBetween([0], [10]);
        Assert.That(view.StartBefore([9]), Is.True);
        Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 3 }));
    }

    [Test]
    public void Multiple_concurrent_readers_do_not_block_each_other_or_a_writer()
    {
        _db.Set([1], [1]);

        // LMDB's MVCC model: unlimited concurrent readers (each its own read-only transaction), one writer at a
        // time via the group-commit coordinator.
        Parallel.For(0, 8, _ =>
        {
            Assert.That(_db.Get([1]), Is.EqualTo(new byte[] { 1 }));
        });

        _db.Set([2], [2]);
        Assert.That(_db.Get([2]), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void Concurrent_point_writes_from_multiple_threads_all_succeed()
    {
        // Regression: LMDB allows only one write transaction per environment at a time, and the verification
        // spike confirmed BeginTransaction() blocks the caller rather than throwing a busy error - see
        // PLAN-LMDB.md "Spike findings" #1. Set() must use that blocking acquire (via SubmitWrite/DrainIfLeader),
        // so none of these concurrent writers should ever throw.
        const int writerCount = 32;
        Parallel.For(0, writerCount, i => _db.Set(BitConverter.GetBytes(i), BitConverter.GetBytes(i)));

        for (int i = 0; i < writerCount; i++)
        {
            Assert.That(_db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
        }
    }

    [Test]
    public void Concurrent_write_batches_from_multiple_threads_all_succeed()
    {
        const int writerCount = 32;
        Parallel.For(0, writerCount, i =>
        {
            using IWriteBatch batch = _db.StartWriteBatch();
            batch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
        });

        for (int i = 0; i < writerCount; i++)
        {
            Assert.That(_db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
        }
    }

    [Test]
    public void Stress_many_concurrent_mixed_writers_are_all_committed_and_visible()
    {
        // Heavier, mixed-shape stress for the group-commit coordinator (SubmitWrite/DrainIfLeader): many
        // concurrent point writes and multi-key write-batches racing for leadership, repeated across several
        // rounds to increase the chance of exercising the leader-handoff retry window documented on
        // DrainIfLeader. A lost write here would mean the coordinator stranded a caller's PendingWrite.
        const int rounds = 5;
        const int writersPerRound = 200;

        for (int round = 0; round < rounds; round++)
        {
            int roundOffset = round * writersPerRound;
            Parallel.For(0, writersPerRound, i =>
            {
                int value = roundOffset + i;
                byte[] key = BitConverter.GetBytes(value);
                if (i % 2 == 0)
                {
                    _db.Set(key, key);
                }
                else
                {
                    using IWriteBatch batch = _db.StartWriteBatch();
                    batch.PutSpan(key, key);
                    batch.PutSpan(BitConverter.GetBytes(-value - 1), key);
                }
            });
        }

        for (int round = 0; round < rounds; round++)
        {
            int roundOffset = round * writersPerRound;
            for (int i = 0; i < writersPerRound; i++)
            {
                int value = roundOffset + i;
                byte[] key = BitConverter.GetBytes(value);
                Assert.That(_db.Get(key), Is.EqualTo(key));
                if (i % 2 != 0)
                {
                    Assert.That(_db.Get(BitConverter.GetBytes(-value - 1)), Is.EqualTo(key));
                }
            }
        }
    }

    [Test]
    public void GatherMetric_reports_a_positive_size_after_writes()
    {
        _db.Set(TestItem.KeccakA.Bytes, new byte[1024]);
        _db.Flush();

        Assert.That(_db.GatherMetric().Size, Is.GreaterThan(0));
    }

    [Test]
    public void EstimatedCount_reflects_the_number_of_stored_keys()
    {
        Assert.That(_db.EstimatedCount, Is.EqualTo(0));

        _db.Set(TestItem.KeccakA.Bytes, [1]);
        _db.Set(TestItem.KeccakB.Bytes, [2]);

        Assert.That(_db.EstimatedCount, Is.EqualTo(2));
    }

    [Test]
    public void CreateSnapshot_is_isolated_from_writes_made_after_it_was_taken()
    {
        _db.Set(TestItem.KeccakA.Bytes, [1]);

        using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        _db.Set(TestItem.KeccakA.Bytes, [2]);
        _db.Set(TestItem.KeccakB.Bytes, [3]);

        Assert.That(snapshot.Get(TestItem.KeccakA.Bytes), Is.EqualTo(new byte[] { 1 }));
        Assert.That(snapshot.Get(TestItem.KeccakB.Bytes), Is.Null);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void Snapshot_supports_sorted_reads_since_FlatDb_persistence_casts_it_to_ISortedKeyValueStore()
    {
        // RocksDbPersistence.CreateReader/CreateWriteBatch cast snapshot.GetColumn(Account/Storage) to
        // ISortedKeyValueStore - a snapshot that only supported point Get would crash FlatDb at startup.
        for (byte i = 0; i < 5; i++)
        {
            _db.Set([i, i, i], [i, i, i]);
        }

        using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;

        Assert.That(sortedSnapshot.FirstKey, Is.EqualTo(new byte[] { 0, 0, 0 }));
        Assert.That(sortedSnapshot.LastKey, Is.EqualTo(new byte[] { 4, 4, 4 }));

        using ISortedView view = sortedSnapshot.GetViewBetween([1, 0, 0], [4, 0, 0]);
        byte expected = 1;
        while (view.MoveNext())
        {
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { expected, expected, expected }));
            expected++;
        }

        Assert.That(expected, Is.EqualTo((byte)4));
    }

    [Test]
    public void Snapshot_supports_multiple_independent_views_and_point_reads_concurrently()
    {
        for (byte i = 0; i < 5; i++)
        {
            _db.Set([i], [i]);
        }

        using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;

        using ISortedView firstView = sortedSnapshot.GetViewBetween([0], [3]);
        using ISortedView secondView = sortedSnapshot.GetViewBetween([2], [5]);

        Assert.That(snapshot.Get([1]), Is.EqualTo(new byte[] { 1 }));
        Assert.That(firstView.MoveNext(), Is.True);
        Assert.That(secondView.MoveNext(), Is.True);
        Assert.That(firstView.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 0 }));
        Assert.That(secondView.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void CreateSnapshot_pool_transactions_all_see_the_identical_pinned_version_under_concurrent_writes()
    {
        // Regression: CreateSnapshot opens a POOL of independent read-only transactions (one per concurrent
        // reader - see LmdbDbSnapshot's remarks), not one shared transaction. All pooled transactions must
        // observe the exact same committed version, or different pool members could serve one snapshot's readers
        // inconsistent values. They are opened back-to-back under LmdbDb._writeCommitGate, the same lock
        // DrainIfLeader takes around its own Commit() call, so a commit can never become visible partway through
        // pool creation. This drives real concurrent writes against real concurrent CreateSnapshot/Get calls to
        // actually exercise that coordination, rather than just asserting it exists.
        byte[] key = [0];
        _db.Set(key, BitConverter.GetBytes(0));

        using ManualResetEventSlim stopWriting = new();
        Task writer = Task.Run(() =>
        {
            int value = 1;
            while (!stopWriting.IsSet)
            {
                _db.Set(key, BitConverter.GetBytes(value++));
            }
        });

        try
        {
            for (int iteration = 0; iteration < 50; iteration++)
            {
                using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();

                byte[]?[] seenValues = new byte[Environment.ProcessorCount * 2][];
                Parallel.For(0, seenValues.Length, i => seenValues[i] = snapshot.Get(key));

                // Every reader through this one snapshot must see the identical value, whichever pool member
                // happened to serve it - a version split across the pool would show up as differing values.
                Assert.That(seenValues, Has.All.EqualTo(seenValues[0]));
            }
        }
        finally
        {
            stopWriting.Set();
            writer.Wait();
        }
    }

    [Test]
    public void Snapshot_survives_real_multi_threaded_concurrent_reads()
    {
        // Written before the reader-pool code, per PLAN-LMDB.md's "Testing" section: this is the regression that
        // would have caught BTDB bug #7 (silent corruption from concurrent multi-threaded use of one shared
        // transaction/cursor state) immediately in CI, rather than reactively after a live-node crash weeks
        // later. LmdbDb.CreateSnapshot's pool-of-independent-transactions design exists specifically so this
        // passes: every thread below gets its own pooled transaction/cursor for the call's duration, so no two
        // threads ever touch the same one concurrently.
        const int keyCount = 64;
        for (byte i = 0; i < keyCount; i++)
        {
            _db.Set([i], [i]);
        }

        using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;

        Parallel.For(0, keyCount, i =>
        {
            byte key = (byte)i;
            Assert.That(snapshot.Get([key]), Is.EqualTo(new byte[] { key }));

            using ISortedView view = sortedSnapshot.GetViewBetween([key], [(byte)keyCount]);
            Assert.That(view.MoveNext(), Is.True);
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { key }));
        });
    }

    [Test]
    public void Write_beyond_the_initial_MapSize_grows_the_map_and_succeeds()
    {
        // Regression for LmdbDb.CommitWaveWithMapGrowthRetry - see PLAN-LMDB.md "MapSize sizing and growth".
        // Opens its own tiny-MapSize instance so a modest write volume actually exhausts it, forcing at least one
        // MDBResultCode.MapFull retry-and-grow cycle, rather than relying on the fixture's generously-sized db.
        const long tinyMapSize = 128 * 1024; // small enough that a few KB of writes will exceed it at least once
        string path = Path.Combine("testdb-lmdb-mapgrowth", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("growth-test", path, tinyMapSize, TestMaxReaders, TestMaxOperationsPerWave, LimboLogs.Instance);
        try
        {
            const int count = 200;
            for (int i = 0; i < count; i++)
            {
                byte[] key = BitConverter.GetBytes(i);
                byte[] value = new byte[256];
                value.AsSpan().Fill((byte)i);
                db.Set(key, value);
            }

            for (int i = 0; i < count; i++)
            {
                byte[] expected = new byte[256];
                expected.AsSpan().Fill((byte)i);
                Assert.That(db.Get(BitConverter.GetBytes(i)), Is.EqualTo(expected));
            }
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    // Deliberately no test exercises MDBResultCode.ReadersFull itself: the verification spike (see PLAN-LMDB.md
    // "Known issues") confirmed that in LightningDB 0.23.0, a *failed* BeginTransaction call leaks an internal
    // transaction handle the library still tracks against its environment - one that cannot be disposed because
    // no caller ever receives a reference to it - and that handle later crashes the *entire process* via an
    // unrecoverable finalizer exception, on some unrelated later GC, regardless of whether a subsequent retry
    // succeeds. Deliberately reproducing that condition in a test would make the test process itself liable to
    // crash (and did, during this change's own development). The test below instead validates the actual fix
    // (generous MaxReaders headroom) without ever tripping the underlying library bug.

    [Test]
    public void CreateSnapshot_supports_many_concurrent_snapshots_without_exhausting_the_reader_table()
    {
        // The actual fix for the failure above: ILmdbConfig.MaxReaders defaults to 8192, not LMDB's own default of
        // 126 - comfortably enough headroom for several concurrently-alive snapshots (each holding
        // Environment.ProcessorCount readers) even on a many-core machine, which is exactly the shape
        // RocksDbPersistence.CreateReader/CreateWriteBatch's per-call CreateSnapshot usage produces in practice.
        int poolSize = Environment.ProcessorCount;
        int snapshotsToHoldConcurrently = Math.Max(4, (126 / Math.Max(poolSize, 1)) + 2); // would exceed LMDB's own default of 126
        string path = Path.Combine("testdb-lmdb-manysnapshots", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("many-snapshots-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, LimboLogs.Instance);
        try
        {
            db.Set([1], [1]);

            List<IKeyValueStoreSnapshot> snapshots = [];
            try
            {
                for (int i = 0; i < snapshotsToHoldConcurrently; i++)
                {
                    snapshots.Add(((IKeyValueStoreWithSnapshot)db).CreateSnapshot());
                }

                Assert.That(snapshots, Has.Count.EqualTo(snapshotsToHoldConcurrently));
                Assert.That(snapshots[^1].Get([1]), Is.EqualTo(new byte[] { 1 }));
            }
            finally
            {
                foreach (IKeyValueStoreSnapshot snapshot in snapshots) snapshot.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void TakeAndResetDiagnostics_reports_call_counts_and_resets_between_calls()
    {
        _db.Set([1], [1]);
        _db.Get([1]);
        _db.Get([1]);
        using (IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot()) { }

        LmdbDb.DiagnosticsSnapshot first = _db.TakeAndResetDiagnostics();
        Assert.That(first.GetCount, Is.EqualTo(2));
        Assert.That(first.WriteWaveCount, Is.EqualTo(1));
        Assert.That(first.CreateSnapshotCount, Is.EqualTo(1));
        Assert.That(first.AvgGetMicros, Is.GreaterThanOrEqualTo(0));
        Assert.That(first.AvgCreateSnapshotMicros, Is.GreaterThanOrEqualTo(0));
        Assert.That(first.AvgWriteWaveMicros, Is.GreaterThanOrEqualTo(0));
        Assert.That(first.AvgWriteWaveOps, Is.EqualTo(1));
        Assert.That(first.MapGrowthCount, Is.EqualTo(0));
        // The snapshot above was already disposed (empty `using` body) before TakeAndResetDiagnostics ran.
        Assert.That(first.AliveSnapshotPoolReaders, Is.EqualTo(0));

        // A second call with no activity in between must report an empty interval, not the same counts again -
        // this is what makes the periodic logger's "last ~30s" numbers meaningful on a long-running node.
        // AliveSnapshotPoolReaders is a gauge, not a reset counter, but it's still 0 here since nothing is alive.
        LmdbDb.DiagnosticsSnapshot second = _db.TakeAndResetDiagnostics();
        Assert.That(second, Is.EqualTo(new LmdbDb.DiagnosticsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0)));

        _db.Get([1]);
        LmdbDb.DiagnosticsSnapshot third = _db.TakeAndResetDiagnostics();
        Assert.That(third.GetCount, Is.EqualTo(1));
    }

    [Test]
    public void AliveSnapshotPoolReaders_tracks_currently_open_pooled_readers_across_multiple_snapshots()
    {
        // A gauge, not a reset-on-read counter (unlike the other DiagnosticsSnapshot fields) - see
        // PLAN-LMDB.md "Known issues": logged alongside slow-event warnings to check whether LMDB's documented
        // "write-commit cost scales with active reader count" behavior explains a given stall.
        int poolSize = Environment.ProcessorCount;
        Assert.That(_db.TakeAndResetDiagnostics().AliveSnapshotPoolReaders, Is.EqualTo(0));

        IKeyValueStoreSnapshot first = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        Assert.That(_db.TakeAndResetDiagnostics().AliveSnapshotPoolReaders, Is.EqualTo(poolSize));

        IKeyValueStoreSnapshot second = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
        Assert.That(_db.TakeAndResetDiagnostics().AliveSnapshotPoolReaders, Is.EqualTo(poolSize * 2));

        first.Dispose();
        Assert.That(_db.TakeAndResetDiagnostics().AliveSnapshotPoolReaders, Is.EqualTo(poolSize));

        second.Dispose();
        Assert.That(_db.TakeAndResetDiagnostics().AliveSnapshotPoolReaders, Is.EqualTo(0));
    }

    [Test]
    public void MaxOperationsPerWave_splits_many_separate_write_batches_across_multiple_physical_commits()
    {
        // Regression for ILmdbConfig.MaxOperationsPerWave - see PLAN-LMDB.md "Known issues": an unbounded wave
        // lets many small concurrent writers coalesce into one arbitrarily large physical commit, which blocks
        // every concurrent CreateSnapshot caller (not just other writers) for that commit's entire duration.
        // With a tiny cap, 20 single-operation concurrent writers must span at least 5 separate physical commits
        // (ceil(20/4)), not get coalesced into one or a handful - and every write must still land correctly
        // regardless of which physical commit it ended up in.
        const int tinyWaveCap = 4;
        string path = Path.Combine("testdb-lmdb-wavecap", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("wave-cap-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, LimboLogs.Instance);
        try
        {
            const int writerCount = 20;
            Parallel.For(0, writerCount, i => db.Set(BitConverter.GetBytes(i), BitConverter.GetBytes(i)));

            for (int i = 0; i < writerCount; i++)
            {
                Assert.That(db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
            }

            LmdbDb.DiagnosticsSnapshot diagnostics = db.TakeAndResetDiagnostics();
            Assert.That(diagnostics.WriteWaveCount, Is.GreaterThanOrEqualTo(writerCount / tinyWaveCap));
            Assert.That(diagnostics.AvgWriteWaveOps, Is.LessThanOrEqualTo(tinyWaveCap));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void MaxOperationsPerWave_splits_a_single_callers_oversized_write_batch_into_sequential_chunks()
    {
        // A single caller's write batch that BY ITSELF exceeds the cap is split into sequential physical
        // commits (CommitLargeWriteInChunks) rather than committed uncapped - see PLAN-LMDB.md "Known issues"
        // for why (an unbounded single commit, observed in practice for snap-sync account-tree writes averaging
        // ~42,000 operations, blocks every concurrent CreateSnapshot reader for that commit's entire duration).
        // This trades away that batch's previous all-or-nothing atomicity across chunks - accepted because the
        // callers that actually produce oversized batches already tolerate losing the whole thing on a crash via
        // WriteFlags.DisableWAL, so tolerating a rare partial application here is no worse.
        const int tinyWaveCap = 4;
        const int opsInOneBatch = 50;
        string path = Path.Combine("testdb-lmdb-wavecap-split", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("wave-cap-split-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, LimboLogs.Instance);
        try
        {
            using (IWriteBatch batch = db.StartWriteBatch())
            {
                for (int i = 0; i < opsInOneBatch; i++)
                {
                    batch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
                }
            }

            for (int i = 0; i < opsInOneBatch; i++)
            {
                Assert.That(db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
            }

            LmdbDb.DiagnosticsSnapshot diagnostics = db.TakeAndResetDiagnostics();
            int expectedChunks = (opsInOneBatch + tinyWaveCap - 1) / tinyWaveCap;
            Assert.That(diagnostics.WriteWaveCount, Is.EqualTo(expectedChunks));
            Assert.That(diagnostics.AvgWriteWaveOps, Is.LessThanOrEqualTo(tinyWaveCap));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void MaxOperationsPerWave_does_not_split_a_single_callers_write_batch_under_the_cap()
    {
        const int tinyWaveCap = 4;
        const int opsInOneBatch = 3;
        string path = Path.Combine("testdb-lmdb-wavecap-nosplit", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("wave-cap-nosplit-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, LimboLogs.Instance);
        try
        {
            using (IWriteBatch batch = db.StartWriteBatch())
            {
                for (int i = 0; i < opsInOneBatch; i++)
                {
                    batch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
                }
            }

            for (int i = 0; i < opsInOneBatch; i++)
            {
                Assert.That(db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
            }

            LmdbDb.DiagnosticsSnapshot diagnostics = db.TakeAndResetDiagnostics();
            Assert.That(diagnostics.WriteWaveCount, Is.EqualTo(1));
            Assert.That(diagnostics.AvgWriteWaveOps, Is.EqualTo(opsInOneBatch));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void CommitLargeWriteInChunks_yields_to_other_queued_writes_between_chunks()
    {
        // Regression: without yielding between chunks, the single leader thread processed one oversized
        // write's ENTIRE multi-chunk sequence before ever looking at the queue again, starving every other
        // queued write (however trivial) for that whole duration - observed in production as many small,
        // unrelated writes measuring 100-300ms in near-identical clusters, all queued behind one in-flight
        // ~40,000-operation write on an unrelated column. Drives a genuinely large number of chunks and
        // concurrent small writers to make the interleaving this fix restores overwhelmingly likely to show
        // up in completion order, without depending on exact timing.
        const int tinyWaveCap = 4;
        const int chunksInLargeWrite = 200;
        const int opsInLargeWrite = tinyWaveCap * chunksInLargeWrite;
        const int smallWriterCount = 32;
        string path = Path.Combine("testdb-lmdb-wavecap-fairness", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("wave-cap-fairness-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, LimboLogs.Instance);
        try
        {
            ConcurrentQueue<string> completionOrder = new();
            using Barrier barrier = new(1 + smallWriterCount);

            Task largeWriteTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                using (IWriteBatch batch = db.StartWriteBatch())
                {
                    for (int i = 0; i < opsInLargeWrite; i++)
                    {
                        batch.PutSpan(BitConverter.GetBytes(i), BitConverter.GetBytes(i));
                    }
                }

                completionOrder.Enqueue("large");
            });

            Task[] smallWriteTasks = new Task[smallWriterCount];
            for (int t = 0; t < smallWriterCount; t++)
            {
                int key = 1_000_000 + t;
                smallWriteTasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    db.Set(BitConverter.GetBytes(key), BitConverter.GetBytes(key));
                    completionOrder.Enqueue($"small-{key}");
                });
            }

            Task.WaitAll([largeWriteTask, .. smallWriteTasks]);

            // Correctness first: every write must land regardless of how they interleaved.
            for (int i = 0; i < opsInLargeWrite; i++)
            {
                Assert.That(db.Get(BitConverter.GetBytes(i)), Is.EqualTo(BitConverter.GetBytes(i)));
            }

            for (int t = 0; t < smallWriterCount; t++)
            {
                int key = 1_000_000 + t;
                Assert.That(db.Get(BitConverter.GetBytes(key)), Is.EqualTo(BitConverter.GetBytes(key)));
            }

            // Fairness: with the old monopolizing behavior, EVERY small write would appear strictly after
            // "large" in completion order (the leader could not service anything else until the whole
            // 200-chunk sequence finished). At least one completing first is direct evidence of interleaving.
            List<string> order = [.. completionOrder];
            int largeIndex = order.IndexOf("large");
            Assert.That(largeIndex, Is.GreaterThan(0),
                "No small write completed before the large write finished its entire chunk sequence - it monopolized the writer role instead of yielding between chunks.");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
