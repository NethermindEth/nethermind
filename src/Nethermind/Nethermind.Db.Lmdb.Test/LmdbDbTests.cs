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
        _db = new LmdbDb("test", DbPath, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance);
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

        _db = new LmdbDb("test", DbPath, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance);
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
        // PLAN-LMDB.md "Spike findings" #1. Set() must use that blocking acquire (via SubmitWrite, drained by the
        // dedicated writer thread - see WriterThreadLoop), so none of these concurrent writers should ever throw.
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
        // Heavier, mixed-shape stress for the dedicated writer thread (SubmitWrite/WriterThreadLoop): many
        // concurrent point writes and multi-key write-batches enqueuing at once, repeated across several rounds
        // to increase the chance of exercising the reset-before-drain race documented on WriterThreadLoop. A lost
        // write here would mean the writer thread stranded a caller's PendingWrite.
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
    public void A_single_dedicated_writer_thread_is_started_on_construction_and_joined_cleanly_on_dispose()
    {
        // The defining property of the dedicated-writer-thread redesign (see LmdbDb.WriterThreadLoop's remarks) -
        // a real, named, alive background thread exists per instance (not a race among arbitrary caller threads),
        // and Dispose() must join it cleanly rather than leaving it running or hanging the caller.
        Assert.That(_db.IsWriterThreadAlive, Is.True);
        Assert.That(_db.WriterThreadName, Is.EqualTo("Lmdb.Writer.test"));

        // A write submitted and committed before Dispose() proves the thread actually drains the queue, not just
        // that it exists.
        byte[] value = TestItem.KeccakA.BytesToArray();
        _db.Set(TestItem.KeccakA.Bytes, value);
        Assert.That(_db.Get(TestItem.KeccakA.Bytes), Is.EqualTo(value));

        _db.Dispose();
        Assert.That(_db.IsWriterThreadAlive, Is.False, "Dispose should Join the writer thread, not merely signal it to stop.");
    }

    [Test]
    public void GatherMetric_reports_a_positive_size_after_writes()
    {
        _db.Set(TestItem.KeccakA.Bytes, new byte[1024]);
        _db.Flush();

        Assert.That(_db.GatherMetric().Size, Is.GreaterThan(0));
    }

    [Test]
    public void GetEnvironmentStats_reports_real_entries_and_a_sane_default_page_size_for_a_single_database()
    {
        // _db is partitionCount: 1 (single unnamed database) - mdb_env_stat's root database IS the actual data
        // in this case (see LmdbDb.GetEnvironmentStats's remarks), so Entries should match real writes exactly.
        for (int i = 0; i < 10; i++) _db.Set(BitConverter.GetBytes(i), BitConverter.GetBytes(i));

        LightningDB.Stats stats = _db.GetEnvironmentStats();
        Assert.That(stats.Entries, Is.EqualTo(10));
        Assert.That(stats.PageSize, Is.AnyOf(512, 1024, 2048, 4096, 8192, 16384, 32768, 65536), "page size should be a power of 2 in LMDB's valid range even at the OS default.");
        Assert.That(stats.LeafPages, Is.GreaterThan(0));
    }

    [Test]
    public void GetPerPartitionStats_matches_GetEnvironmentStats_in_the_single_database_case()
    {
        for (int i = 0; i < 10; i++) _db.Set(BitConverter.GetBytes(i), BitConverter.GetBytes(i));

        LightningDB.Stats envStats = _db.GetEnvironmentStats();
        LightningDB.Stats perPartitionStats = _db.GetPerPartitionStats();
        Assert.That(perPartitionStats.Entries, Is.EqualTo(envStats.Entries));
        Assert.That(perPartitionStats.PageSize, Is.EqualTo(envStats.PageSize));
        Assert.That(perPartitionStats.LeafPages, Is.EqualTo(envStats.LeafPages));
    }

    [Test]
    public void GetPerPartitionStats_sees_real_partitioned_data_that_GetEnvironmentStats_cannot()
    {
        // The entire reason GetPerPartitionStats exists: mdb_env_stat reports the environment's ROOT database,
        // which for a partitioned (named-database) environment is just the tiny catalog mapping partition names
        // to their own separate sub-databases - not the actual data. mdb_stat per named database (what
        // GetPerPartitionStats uses) sees the real thing. This is the scenario LmdbColumnsDbAdapter's periodic
        // logger must get right for a partitioned Account column - see its own FormatStats remarks.
        string path = Path.Combine("testdb-lmdb-partition-stats", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 4);
        try
        {
            for (int i = 0; i < 1000; i++)
            {
                byte[] key = [(byte)(i % 4 * 64), 0, 0, (byte)i, (byte)(i >> 8)];
                db.Set(key, key);
            }

            LightningDB.Stats envStats = db.GetEnvironmentStats();
            LightningDB.Stats perPartitionStats = db.GetPerPartitionStats();

            Assert.That(envStats.Entries, Is.LessThan(perPartitionStats.Entries),
                "mdb_env_stat should see only the small partition-name catalog (4 entries), not the real data.");
            Assert.That(envStats.Entries, Is.EqualTo(4), "the catalog has exactly one entry per named partition database.");
            Assert.That(perPartitionStats.Entries, Is.EqualTo(1000), "mdb_stat per partition, summed, must see every real entry.");
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void PageSizeBytes_override_is_honored_for_a_brand_new_environment()
    {
        string path = Path.Combine("testdb-lmdb-pagesize", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = new("pagesize-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance, pageSizeBytes: 8192);
        try
        {
            db.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
            Assert.That(db.GetEnvironmentStats().PageSize, Is.EqualTo(8192));
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
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
        using LmdbDb db = new("growth-test", path, tinyMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance);
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
        using LmdbDb db = new("many-snapshots-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance);
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
        Assert.That(second, Is.EqualTo(new LmdbDb.DiagnosticsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)));

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
        using LmdbDb db = new("wave-cap-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, partitionCount: 1, LimboLogs.Instance);
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
        using LmdbDb db = new("wave-cap-split-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, partitionCount: 1, LimboLogs.Instance);
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
        using LmdbDb db = new("wave-cap-nosplit-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, partitionCount: 1, LimboLogs.Instance);
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
    public void CanUseAppendData_engages_for_an_ascending_batch_into_an_empty_database()
    {
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 0; i < 10; i++) batch.PutSpan([(byte)i], [(byte)i]);
        }

        for (int i = 0; i < 10; i++) Assert.That(_db.Get([(byte)i]), Is.EqualTo(new byte[] { (byte)i }));

        LmdbDb.DiagnosticsSnapshot diagnostics = _db.TakeAndResetDiagnostics();
        Assert.That(diagnostics.TotalPutCount, Is.EqualTo(10));
        Assert.That(diagnostics.AppendedPutCount, Is.EqualTo(10));
    }

    [Test]
    public void CanUseAppendData_falls_back_for_a_batch_that_is_not_ascending()
    {
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 9; i >= 0; i--) batch.PutSpan([(byte)i], [(byte)i]);
        }

        for (int i = 0; i < 10; i++) Assert.That(_db.Get([(byte)i]), Is.EqualTo(new byte[] { (byte)i }));

        LmdbDb.DiagnosticsSnapshot diagnostics = _db.TakeAndResetDiagnostics();
        Assert.That(diagnostics.TotalPutCount, Is.EqualTo(10));
        Assert.That(diagnostics.AppendedPutCount, Is.EqualTo(0));
    }

    [Test]
    public void CanUseAppendData_checks_the_databases_actual_current_max_key_not_just_batch_order()
    {
        // Regression for the exact mistake this feature almost shipped with: checking only that a batch is
        // ascending among its own keys is not sufficient - LMDB's AppendData requires every appended key to
        // exceed the database's actual current max key. A batch that is internally ascending but reaches
        // backwards into already-written keyspace (e.g. snap sync's healing phase, or an out-of-order
        // concurrent completion) must not use it, regardless of its own internal order.
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 100; i < 110; i++) batch.PutSpan([(byte)i], [(byte)i]);
        }
        Assert.That(_db.TakeAndResetDiagnostics().AppendedPutCount, Is.EqualTo(10));

        // Ascending among themselves, but every key is smaller than the database's current max (109) - must
        // not append, or this would silently corrupt the database per LMDB's own documented semantics.
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 0; i < 10; i++) batch.PutSpan([(byte)i], [(byte)i]);
        }
        LmdbDb.DiagnosticsSnapshot second = _db.TakeAndResetDiagnostics();
        Assert.That(second.TotalPutCount, Is.EqualTo(10));
        Assert.That(second.AppendedPutCount, Is.EqualTo(0));

        // Genuinely beyond the database's current max again - append re-engages.
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 200; i < 210; i++) batch.PutSpan([(byte)i], [(byte)i]);
        }
        Assert.That(_db.TakeAndResetDiagnostics().AppendedPutCount, Is.EqualTo(10));

        for (int i = 0; i < 10; i++) Assert.That(_db.Get([(byte)i]), Is.EqualTo(new byte[] { (byte)i }));
        for (int i = 100; i < 110; i++) Assert.That(_db.Get([(byte)i]), Is.EqualTo(new byte[] { (byte)i }));
        for (int i = 200; i < 210; i++) Assert.That(_db.Get([(byte)i]), Is.EqualTo(new byte[] { (byte)i }));
    }

    [Test]
    public void CanUseAppendData_ignores_deletes_when_checking_ascending_put_order()
    {
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            batch.PutSpan([1], [1]);
            batch.Remove([200]); // a delete does not add a key, so it must not break the puts' ascending order
            batch.PutSpan([2], [2]);
            batch.PutSpan([3], [3]);
        }

        Assert.That(_db.Get([1]), Is.EqualTo(new byte[] { 1 }));
        Assert.That(_db.Get([2]), Is.EqualTo(new byte[] { 2 }));
        Assert.That(_db.Get([3]), Is.EqualTo(new byte[] { 3 }));

        LmdbDb.DiagnosticsSnapshot diagnostics = _db.TakeAndResetDiagnostics();
        Assert.That(diagnostics.TotalPutCount, Is.EqualTo(3));
        Assert.That(diagnostics.AppendedPutCount, Is.EqualTo(3));
    }

    [Test]
    public void Diagnostics_split_apply_time_by_appended_vs_non_appended_per_operation()
    {
        // Added specifically to get a true live A/B signal for AppendData's real effect without needing a
        // toggle or a second sync run - see PLAN-LMDB.md "Known issues". A single wave can contain both an
        // appended and a non-appended partition-group side by side (exactly what happens live whenever
        // AppendData's self-verifying check accepts one group and declines another), so this must track them as
        // two independent, correctly-attributed buckets rather than one blended apply-time average.
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 0; i < 10; i++) batch.PutSpan([(byte)i], [(byte)i]);
        }
        LmdbDb.DiagnosticsSnapshot firstBatch = _db.TakeAndResetDiagnostics();
        Assert.That(firstBatch.AppendedApplyOpsCount, Is.EqualTo(10), "An ascending batch into an empty DB must be fully attributed to the appended bucket.");
        Assert.That(firstBatch.NonAppendedApplyOpsCount, Is.EqualTo(0));
        Assert.That(firstBatch.AvgAppendedApplyMicrosPerOp, Is.GreaterThanOrEqualTo(0));

        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            // Every key here is below the DB's current max (9) - must fall entirely into the non-appended bucket.
            for (int i = 0; i < 5; i++) batch.PutSpan([(byte)i], [(byte)(i + 100)]);
        }
        LmdbDb.DiagnosticsSnapshot secondBatch = _db.TakeAndResetDiagnostics();
        Assert.That(secondBatch.AppendedApplyOpsCount, Is.EqualTo(0));
        Assert.That(secondBatch.NonAppendedApplyOpsCount, Is.EqualTo(5), "A batch entirely below the DB's current max must be fully attributed to the non-appended bucket.");
        Assert.That(secondBatch.AvgNonAppendedApplyMicrosPerOp, Is.GreaterThanOrEqualTo(0));
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
        using LmdbDb db = new("wave-cap-fairness-test", path, TestMapSize, TestMaxReaders, tinyWaveCap, partitionCount: 1, LimboLogs.Instance);
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

    // Partitioning (ILmdbConfig.EnableAccountPartitioning) - see PLAN-LMDB.md "Known issues". Keys below are
    // constructed with an explicit 4-byte leading prefix so their partition is obvious from the test itself,
    // mirroring how GetPartitionIndex only ever looks at a key's first 4 bytes (big-endian).

    private static LmdbDb NewPartitionedDb(string path, int partitionCount) =>
        new("partitioned-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount, LimboLogs.Instance);

    [Test]
    public void Partitioning_keeps_every_key_retrievable_regardless_of_which_partition_it_lands_in()
    {
        string path = Path.Combine("testdb-lmdb-partition-roundtrip", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 4);
        try
        {
            // 4 partitions over a 32-bit leading prefix: boundaries at 0x00000000, 0x40000000, 0x80000000, 0xC0000000.
            byte[][] keys =
            [
                [0x00, 0x00, 0x00, 0x01],
                [0x3F, 0xFF, 0xFF, 0xFF],
                [0x40, 0x00, 0x00, 0x00],
                [0x7F, 0xFF, 0xFF, 0xFF],
                [0x80, 0x00, 0x00, 0x00],
                [0xBF, 0xFF, 0xFF, 0xFF],
                [0xC0, 0x00, 0x00, 0x00],
                [0xFF, 0xFF, 0xFF, 0xFF],
            ];

            using (IWriteBatch batch = db.StartWriteBatch())
            {
                foreach (byte[] key in keys) batch.PutSpan(key, key);
            }

            foreach (byte[] key in keys) Assert.That(db.Get(key), Is.EqualTo(key));
            Assert.That(db.EstimatedCount, Is.EqualTo(keys.Length));
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Partitioning_merges_GetAll_FirstKey_and_LastKey_in_ascending_order_across_partitions()
    {
        string path = Path.Combine("testdb-lmdb-partition-merge", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 4);
        try
        {
            // Inserted deliberately out of key order (and out of partition order) to prove the merge itself -
            // not insertion order - is what produces the ascending result.
            byte[][] insertionOrder =
            [
                [0xC0, 0, 0, 5],
                [0x00, 0, 0, 1],
                [0x80, 0, 0, 3],
                [0x40, 0, 0, 2],
                [0xC0, 0, 0, 9],
                [0x80, 0, 0, 4],
            ];
            using (IWriteBatch batch = db.StartWriteBatch())
            {
                foreach (byte[] key in insertionOrder) batch.PutSpan(key, key);
            }

            byte[][] expectedAscending = [.. insertionOrder.OrderBy(k => k, Bytes.Comparer)];

            Assert.That(db.FirstKey, Is.EqualTo(expectedAscending[0]));
            Assert.That(db.LastKey, Is.EqualTo(expectedAscending[^1]));

            byte[][] actualAscending = [.. db.GetAll(true).Select(kvp => kvp.Key)];
            Assert.That(actualAscending, Is.EqualTo(expectedAscending));
            Assert.That(db.GetAllKeys().ToArray(), Is.EqualTo(expectedAscending));
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Partitioning_lets_a_lower_partition_still_append_after_a_higher_partition_already_has_larger_keys()
    {
        // This is the entire point of partitioning - see ILmdbConfig.EnableAccountPartitioning's remarks. Under
        // a single flat keyspace, only the single highest-keyed partition's writes could ever qualify for
        // AppendData once any higher partition had already advanced past them. With separate databases per
        // partition, each one has its own independent "current max key", so a lower partition's own monotonic
        // stream can still append even after a higher partition is already far ahead.
        string path = Path.Combine("testdb-lmdb-partition-append", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 2);
        try
        {
            byte[] HighKey(byte i) => [0xFF, 0xFF, 0xFF, i];
            byte[] LowKey(byte i) => [0x00, 0x00, 0x00, i];

            using (IWriteBatch batch = db.StartWriteBatch())
            {
                for (byte i = 0; i < 5; i++) batch.PutSpan(HighKey(i), [i]);
            }
            Assert.That(db.TakeAndResetDiagnostics().AppendedPutCount, Is.EqualTo(5));

            using (IWriteBatch batch = db.StartWriteBatch())
            {
                for (byte i = 0; i < 5; i++) batch.PutSpan(LowKey(i), [i]);
            }
            LmdbDb.DiagnosticsSnapshot diagnostics = db.TakeAndResetDiagnostics();
            Assert.That(diagnostics.TotalPutCount, Is.EqualTo(5));
            Assert.That(diagnostics.AppendedPutCount, Is.EqualTo(5));

            for (byte i = 0; i < 5; i++)
            {
                Assert.That(db.Get(HighKey(i)), Is.EqualTo(new byte[] { i }));
                Assert.That(db.Get(LowKey(i)), Is.EqualTo(new byte[] { i }));
            }
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Partitioning_GetViewBetween_iterates_in_order_across_partitions_and_respects_a_sub_range()
    {
        string path = Path.Combine("testdb-lmdb-partition-view", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 4);
        try
        {
            byte[][] keys =
            [
                [0x00, 0, 0, 1], [0x00, 0, 0, 2],
                [0x40, 0, 0, 1], [0x40, 0, 0, 2],
                [0x80, 0, 0, 1], [0x80, 0, 0, 2],
                [0xC0, 0, 0, 1], [0xC0, 0, 0, 2],
            ];
            using (IWriteBatch batch = db.StartWriteBatch())
            {
                foreach (byte[] key in keys) batch.PutSpan(key, key);
            }

            using (ISortedView fullView = db.GetViewBetween([0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF]))
            {
                List<byte[]> actual = [];
                while (fullView.MoveNext()) actual.Add(fullView.CurrentKey.ToArray());
                Assert.That(actual, Is.EqualTo(keys));
            }

            // A sub-range spanning only the middle two partitions (0x40.. and 0x80..) must exclude the outer two.
            using (ISortedView subView = db.GetViewBetween([0x40, 0, 0, 0], [0xC0, 0, 0, 0]))
            {
                List<byte[]> actual = [];
                while (subView.MoveNext()) actual.Add(subView.CurrentKey.ToArray());
                Assert.That(actual, Is.EqualTo(keys[2..6]));
            }
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Partitioning_StartBefore_finds_a_floor_in_a_lower_partition_when_the_seek_values_own_partition_is_empty()
    {
        // No production call site exercises StartBefore against a partitioned column in practice (see
        // PLAN-LMDB.md "Known issues"), but the merge still needs to behave correctly rather than being a known
        // gap: the floor of a seek value can legitimately live in a lower partition than the value's own.
        string path = Path.Combine("testdb-lmdb-partition-startbefore", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 2);
        try
        {
            byte[] onlyKey = [0x00, 0x00, 0x00, 0x05];
            db.Set(onlyKey, onlyKey);

            using ISortedView view = db.GetViewBetween([0x00, 0x00, 0x00, 0x00], [0xFF, 0xFF, 0xFF, 0xFF]);
            // 0x90000000... falls in partition 1 (empty) - the floor must come from partition 0's only key.
            Assert.That(view.StartBefore([0x90, 0x00, 0x00, 0x00]), Is.True);
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(onlyKey));
            Assert.That(view.MoveNext(), Is.False);
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Partitioning_snapshot_Get_and_GetViewBetween_merge_correctly_across_partitions()
    {
        string path = Path.Combine("testdb-lmdb-partition-snapshot", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        using LmdbDb db = NewPartitionedDb(path, partitionCount: 4);
        try
        {
            byte[][] keys = [[0x00, 0, 0, 1], [0x40, 0, 0, 1], [0x80, 0, 0, 1], [0xC0, 0, 0, 1]];
            using (IWriteBatch batch = db.StartWriteBatch())
            {
                foreach (byte[] key in keys) batch.PutSpan(key, key);
            }

            using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)db).CreateSnapshot();
            foreach (byte[] key in keys) Assert.That(snapshot.Get(key), Is.EqualTo(key));

            ISortedKeyValueStore sortedSnapshot = (ISortedKeyValueStore)snapshot;
            Assert.That(sortedSnapshot.FirstKey, Is.EqualTo(keys[0]));
            Assert.That(sortedSnapshot.LastKey, Is.EqualTo(keys[^1]));

            using ISortedView view = sortedSnapshot.GetViewBetween([0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF]);
            List<byte[]> actual = [];
            while (view.MoveNext()) actual.Add(view.CurrentKey.ToArray());
            Assert.That(actual, Is.EqualTo(keys));
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Enabling_partitioning_over_an_existing_nonpartitioned_database_with_data_throws()
    {
        // See ILmdbConfig.EnableAccountPartitioning's remarks - partitioned mode reads/writes separate named
        // databases that a non-partitioned sync never touched, so silently "losing" existing data would be far
        // worse than refusing to start.
        string path = Path.Combine("testdb-lmdb-partition-guard", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        try
        {
            using (LmdbDb db = new("guard-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance))
            {
                db.Set([1], [1]);
            }

            Assert.Throws<InvalidOperationException>(() =>
                new LmdbDb("guard-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 4, LimboLogs.Instance));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Enabling_partitioning_over_an_existing_empty_nonpartitioned_database_does_not_throw()
    {
        // The guard only fires when the default database actually has entries - a brand new directory, or one
        // whose non-partitioned default database was never written to, is safe to switch into partitioned mode.
        string path = Path.Combine("testdb-lmdb-partition-guard-empty", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        try
        {
            new LmdbDb("guard-empty-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 1, LimboLogs.Instance).Dispose();

            using LmdbDb partitioned = new("guard-empty-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 4, LimboLogs.Instance);
            partitioned.Set([1], [1]);
            Assert.That(partitioned.Get([1]), Is.EqualTo(new byte[] { 1 }));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Test]
    public void Reopening_an_already_partitioned_database_with_the_same_partition_count_does_not_throw()
    {
        // Regression test - observed live: restarting a node after a successful partitioned Account sync threw
        // "already has 8 entries in its default (non-partitioned) database" even though partitioning had been on
        // the whole time and 8 matched the configured partition count exactly. Once MaxDatabases > 0, LMDB's own
        // unnamed root database stops holding user data and becomes the catalog mapping each named sub-database
        // to its internal dbi - a normal reopen of an already-partitioned environment legitimately shows
        // partitionCount entries there (one catalog row per partition name), which the guard must not mistake for
        // real leftover non-partitioned data.
        string path = Path.Combine("testdb-lmdb-partition-reopen", TestContext.CurrentContext.Test.ID);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        try
        {
            using (LmdbDb first = new("reopen-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 8, LimboLogs.Instance))
            {
                first.Set([1], [1]);
                first.Set([200], [200]);
            }

            using LmdbDb reopened = new("reopen-test", path, TestMapSize, TestMaxReaders, TestMaxOperationsPerWave, partitionCount: 8, LimboLogs.Instance);
            Assert.That(reopened.Get([1]), Is.EqualTo(new byte[] { 1 }));
            Assert.That(reopened.Get([200]), Is.EqualTo(new byte[] { 200 }));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
