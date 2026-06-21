// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class DbOnTheRocksTests
    {
        private RocksDbConfigFactory _rocksdbConfigFactory;
        private DbConfig _dbConfig = new();
        string DbPath => Path.Combine("testdb", TestContext.CurrentContext.Test.ID);

        [SetUp]
        public void Setup()
        {
            Directory.CreateDirectory(DbPath);
            _rocksdbConfigFactory = new RocksDbConfigFactory(_dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
        }

        [Test]
        public async Task Dispose_while_writing_does_not_cause_access_violation_exception()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("testDispose1", GetRocksDbSettings("testDispose1", "TestDispose1"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            CancellationTokenSource cancelSource = new();
            ManualResetEventSlim firstWriteWait = new();
            firstWriteWait.Reset();
            bool writeCompleted = false;

            Task task = new(() =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    db.Set(Keccak.Zero, new byte[] { 1, 2, 3 });
                    if (i == 0) firstWriteWait.Set();

                    if (cancelSource.IsCancellationRequested)
                    {
                        return;
                    }
                }

                writeCompleted = true;
            });

            task.Start();

            Assert.That(firstWriteWait.Wait(TimeSpan.FromSeconds(1)), Is.True);

            db.Dispose();

            await Task.Delay(100);

            cancelSource.Cancel();
            Assert.That(writeCompleted, Is.False);

            Assert.That(task.IsFaulted, Is.True);
            task.Dispose();
        }

        [Test]
        public void Dispose_wont_cause_ObjectDisposedException_when_batch_is_still_open()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("testDispose2", GetRocksDbSettings("testDispose2", "TestDispose2"), config, _rocksdbConfigFactory, LimboLogs.Instance);
            _ = db.StartWriteBatch();
            db.Dispose();
        }

        [Test]
        public void CanOpenWithFileWarmer()
        {
            IDbConfig config = new DbConfig();
            config.EnableFileWarmer = true;
            {
                using DbOnTheRocks db = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, _rocksdbConfigFactory, LimboLogs.Instance);
                IKeyValueStore asKv = db;
                for (int i = 0; i < 1000; i++)
                {
                    asKv[i.ToBigEndianByteArray()] = i.ToBigEndianByteArray();
                }
            }

            {
                using DbOnTheRocks _ = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, _rocksdbConfigFactory, LimboLogs.Instance);
            }
        }

        [TestCase("compaction_pri=kByCompensatedSize", TestName = "CanOpenWithAdditionalConfig_SingleOption")]
        [TestCase("compaction_pri=kByCompensatedSize;num_levels=4", TestName = "CanOpenWithAdditionalConfig_MultipleOptions")]
        [TestCase("compaction_pri=kSomethingElse", TestName = "CanOpenWithAdditionalConfig_InvalidOption")]
        public void CanOpenWithAdditionalConfig(string opts)
        {
            IDbConfig config = new DbConfig();
            config.AdditionalRocksDbOptions = opts;

            RocksDbConfigFactory configFactory = new(config, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
            using DbOnTheRocks db = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, configFactory, LimboLogs.Instance);
            byte[] key = [1];
            byte[] value = [2];
            db.Set(key, value);
            Assert.That(db.Get(key), Is.EqualTo(value));
        }

        [Test]
        public void HyperClockCacheWrapper_is_a_noop_compatibility_wrapper()
        {
            using HyperClockCacheWrapper cache = new((ulong)10.KiB);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.Handle, Is.EqualTo(IntPtr.Zero));
                Assert.That(cache.GetUsage(), Is.Zero);
            }
        }

        [Test]
        public void Throws_friendly_error_for_existing_rocksdb_store()
        {
            DbSettings settings = GetRocksDbSettings(DbPath, "Blocks");
            string fullPath = DbOnTheRocks.GetFullDbPath(settings.DbPath, DbPath);
            Directory.CreateDirectory(fullPath);
            File.WriteAllText(Path.Combine(fullPath, "CURRENT"), "");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new DbOnTheRocks(DbPath, settings, _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance))!;

            Assert.That(exception.Message, Does.Contain("sync from scratch"));
        }

        [Test]
        public void TestExtractOptions()
        {
            string options = "compression=kSnappyCompression;optimize_filters_for_hits=true;optimize_filters_for_hits=false;memtable_whole_key_filtering=true;memtable_prefix_bloom_size_ratio=0.02;advise_random_on_open=true;block_based_table_factory.block_size=16000;block_based_table_factory.pin_l0_filter_and_index_blocks_in_cache=true;block_based_table_factory.cache_index_and_filter_blocks_with_high_priority=true;block_based_table_factory.format_version=5;block_based_table_factory.index_type=kTwoLevelIndexSearch;block_based_table_factory.partition_filters=true;block_based_table_factory.metadata_block_size=4096;";
            IDictionary<string, string> parsedOptions = DbOnTheRocks.ExtractOptions(options);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(parsedOptions["compression"], Is.EqualTo("kSnappyCompression"));
                Assert.That(parsedOptions["block_based_table_factory.metadata_block_size"], Is.EqualTo("4096"));
                Assert.That(parsedOptions["optimize_filters_for_hits"], Is.EqualTo("false"));
                Assert.That(parsedOptions["memtable_whole_key_filtering"], Is.EqualTo("true"));
            }
        }

        [Test]
        public void TestNormalizeRocksDbOptions_RemovesDuplicateOptimizeFiltersForHits()
        {
            string options = "optimize_filters_for_hits=true;compression=kSnappyCompression;optimize_filters_for_hits=false;";
            string normalized = DbOnTheRocks.NormalizeRocksDbOptions(options);

            Assert.That(normalized, Is.EqualTo("compression=kSnappyCompression;optimize_filters_for_hits=false;"));
        }

        [Test]
        public void TestNormalizeRocksDbOptions_HandlesEmptyString()
        {
            Assert.That(DbOnTheRocks.NormalizeRocksDbOptions(""), Is.EqualTo(""));
            Assert.That(DbOnTheRocks.NormalizeRocksDbOptions(null!), Is.EqualTo(""));
        }

        [Test]
        public void TestNormalizeRocksDbOptions_PreservesStringWithoutDuplicates()
        {
            string options = "compression=kSnappyCompression;block_size=16000;optimize_filters_for_hits=true;";
            string normalized = DbOnTheRocks.NormalizeRocksDbOptions(options);

            Assert.That(normalized, Is.EqualTo(options));
        }

        [Test]
        public void TestNormalizeRocksDbOptions_HandlesMultipleDuplicates()
        {
            string options = "optimize_filters_for_hits=true;foo=bar;optimize_filters_for_hits=false;baz=qux;optimize_filters_for_hits=true;";
            string normalized = DbOnTheRocks.NormalizeRocksDbOptions(options);

            Assert.That(normalized, Is.EqualTo("foo=bar;baz=qux;optimize_filters_for_hits=true;"));
        }

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName) => new(dbName, dbPath)
        {
        };
    }

    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.None)]
    public class DbOnTheRocksDbTests(bool useColumnDb)
    {
        string DbPath => Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
        private IDb _db = null!;
        IDisposable? _dbDisposable = null!;

        private readonly bool _useColumnDb = useColumnDb;

        [SetUp]
        public void Setup()
        {
            RocksDbConfigFactory rocksdbConfigFactory = new(new DbConfig(), new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);

            if (Directory.Exists(DbPath))
            {
                Directory.Delete(DbPath, true);
            }

            Directory.CreateDirectory(DbPath);
            if (_useColumnDb)
            {
                IDbConfig config = new DbConfig();
                ColumnsDb<ReceiptsColumns> columnsDb = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, rocksdbConfigFactory,
                    LimboLogs.Instance, new List<ReceiptsColumns>() { ReceiptsColumns.Blocks });
                _dbDisposable = columnsDb;

                _db = (ColumnDb)columnsDb.GetColumnDb(ReceiptsColumns.Blocks);
            }
            else
            {
                IDbConfig config = new DbConfig();
                _db = new DbOnTheRocks(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, rocksdbConfigFactory, LimboLogs.Instance);
                _dbDisposable = _db;
            }
        }

        [TearDown]
        public void TearDown()
        {
            _db?.Dispose();
            _dbDisposable?.Dispose();
        }

        [Test]
        public void Smoke_test()
        {
            _db[[1, 2, 3]] = [4, 5, 6];
            AssertCanGetViaAllMethod(_db, [1, 2, 3], [4, 5, 6]);

            _db.Set([2, 3, 4], [5, 6, 7], WriteFlags.LowPriority);
            AssertCanGetViaAllMethod(_db, [2, 3, 4], [5, 6, 7]);
        }

        [Test]
        public void Snapshot_test()
        {
            IKeyValueStoreWithSnapshot withSnapshot = (IKeyValueStoreWithSnapshot)_db;

            byte[] key = new byte[] { 1, 2, 3 };

            _db[key] = new byte[] { 4, 5, 6 };
            AssertCanGetViaAllMethod(_db, key, new byte[] { 4, 5, 6 });

            using IKeyValueStoreSnapshot snapshot = withSnapshot.CreateSnapshot();
            AssertCanGetViaAllMethod(snapshot, key, new byte[] { 4, 5, 6 });

            _db.Set(key, new byte[] { 5, 6, 7 });
            AssertCanGetViaAllMethod(_db, key, new byte[] { 5, 6, 7 });

            AssertCanGetViaAllMethod(snapshot, key, new byte[] { 4, 5, 6 });

            Assert.That(_db.KeyExists(new byte[] { 99, 99, 99 }), Is.False);
        }

        [Test]
        public void Snapshot_dispose_cleans_up_read_options()
        {
            IKeyValueStoreWithSnapshot withSnapshot = (IKeyValueStoreWithSnapshot)_db;

            _db[[1, 2, 3]] = [4, 5, 6];

            IKeyValueStoreSnapshot snapshot = withSnapshot.CreateSnapshot();
            AssertCanGetViaAllMethod(snapshot, [1, 2, 3], [4, 5, 6]);

            // Dispose should clean up owned ReadOptions without throwing
            snapshot.Dispose();

            // Double dispose must be safe
            snapshot.Dispose();
        }

        [Test]
        public void Snapshot_sorted_view_survives_snapshot_dispose()
        {
            IKeyValueStoreWithSnapshot withSnapshot = (IKeyValueStoreWithSnapshot)_db;

            _db[[1]] = [1];
            IKeyValueStoreSnapshot snapshot = withSnapshot.CreateSnapshot();
            ISortedView view = ((ISortedKeyValueStore)snapshot).GetViewBetween([0], [9]);

            snapshot.Dispose();

            Assert.That(view.MoveNext(), Is.True);
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(view.CurrentValue.ToArray(), Is.EqualTo(new byte[] { 1 }));
            view.Dispose();
        }

        [Test]
        public void Smoke_test_large_writes_with_nowal()
        {
            IWriteBatch writeBatch = _db.StartWriteBatch();

            for (int i = 0; i < 1000; i++)
            {
                writeBatch.Set(i.ToBigEndianByteArray(), i.ToBigEndianByteArray(), WriteFlags.DisableWAL);
            }

            writeBatch.Dispose();

            for (int i = 0; i < 1000; i++)
            {
                AssertCanGetViaAllMethod(_db, i.ToBigEndianByteArray(), i.ToBigEndianByteArray());
            }
        }

        [Test]
        public void Write_batch_clear_removes_pending_operations_without_clearing_db()
        {
            _db[[1]] = [1];

            IWriteBatch writeBatch = _db.StartWriteBatch();
            writeBatch.Set([2], [2]);
            writeBatch.Clear();
            writeBatch.Dispose();

            Assert.That(_db.Get([1]), Is.EqualTo(new byte[] { 1 }));
            Assert.That(_db.Get([2]), Is.Null);
        }

        [Test]
        public void Write_batch_copies_value_when_queued()
        {
            byte[] value = [1];

            IWriteBatch writeBatch = _db.StartWriteBatch();
            writeBatch.Set([1], value);
            value[0] = 2;
            writeBatch.Dispose();

            Assert.That(_db.Get([1]), Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void Smoke_test_readahead()
        {
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };
            Assert.That(_db.Get(new byte[] { 1, 2, 3 }, ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        [Test]
        public void Smoke_test_many_readahead()
        {
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };
            // Attempt to trigger auto dispose iterator on many usage
            for (int i = 0; i < 1200000; i++)
            {
                Assert.That(_db.Get(new byte[] { 1, 2, 3 }, ReadFlags.HintReadAhead), Is.EqualTo(new byte[] { 4, 5, 6 }));
            }
        }

        [Test]
        public void Smoke_test_span()
        {
            byte[] key = new byte[] { 1, 2, 3 };
            byte[] value = new byte[] { 4, 5, 6 };
            _db.PutSpan(key, value);
            Span<byte> readSpan = _db.GetSpan(key);
            Assert.That(readSpan.ToArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));

            _db.DangerousReleaseMemory(readSpan);
        }

        [Test]
        public void Smoke_test_span_with_memory_manager()
        {
            byte[] key = new byte[] { 1, 2, 3 };
            byte[] value = new byte[] { 4, 5, 6 };
            _db.PutSpan(key, value);
            Span<byte> readSpan = _db.GetSpan(key);
            Assert.That(readSpan.ToArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));

            IMemoryOwner<byte> manager = new DbSpanMemoryManager(_db, readSpan);
            Memory<byte> theMemory = manager.Memory;
            Assert.That(theMemory.ToArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));

            manager.Dispose();
        }

        [Test]
        public void Native_slice_handles_missing_empty_and_existing_values()
        {
            IReadOnlyNativeKeyValueStore nativeDb = (IReadOnlyNativeKeyValueStore)_db;

            ReadOnlySpan<byte> missing = nativeDb.GetNativeSlice([9], out IntPtr missingHandle);
            Assert.That(missing.Length, Is.Zero);
            Assert.That(missingHandle, Is.EqualTo(IntPtr.Zero));

            _db[[1]] = [];
            ReadOnlySpan<byte> empty = nativeDb.GetNativeSlice([1], out IntPtr emptyHandle);
            Assert.That(empty.Length, Is.Zero);
            nativeDb.DangerousReleaseHandle(emptyHandle);

            _db[[2]] = [3, 4];
            ReadOnlySpan<byte> existing = nativeDb.GetNativeSlice([2], out IntPtr existingHandle);
            byte[] existingCopy = existing.ToArray();
            nativeDb.DangerousReleaseHandle(existingHandle);

            Assert.That(existingCopy, Is.EqualTo(new byte[] { 3, 4 }));
        }

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName) => new(dbName, dbPath)
        {
        };

        [Test]
        public void Can_get_all_on_empty() => _ = _db.GetAll().ToList();

        [Test]
        public void Smoke_test_iterator()
        {
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };

            KeyValuePair<byte[], byte[]>[] allValues = _db.GetAll().ToArray()!;
            Assert.That(allValues[0].Key, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(allValues[0].Value, Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        [Test]
        public void IteratorWorks()
        {
            Assert.That(_db, Is.AssignableTo<ISortedKeyValueStore>());
            ISortedKeyValueStore sortedKeyValue = (ISortedKeyValueStore)_db;

            int entryCount = 3;
            byte i;
            for (i = 0; i < entryCount; i++)
            {
                _db[[i, i, i]] = [i, i, i];
            }

            i--;

            void CheckView(ISortedKeyValueStore sortedKeyValueStore)
            {
                Assert.That(sortedKeyValue.FirstKey, Is.EqualTo(new byte[] { 0, 0, 0 }));
                Assert.That(sortedKeyValue.LastKey, Is.EqualTo(new byte[] { (byte)(entryCount - 1), (byte)(entryCount - 1), (byte)(entryCount - 1) }));
                using ISortedView view = sortedKeyValueStore.GetViewBetween([0], [9]);

                i = 0;
                while (view.MoveNext())
                {
                    Assert.That(view.CurrentKey.ToArray(), Is.EqualTo([i, i, i]));
                    Assert.That(view.CurrentValue.ToArray(), Is.EqualTo([i, i, i]));
                    i++;
                }

                Assert.That(i, Is.EqualTo((byte)entryCount));
            }

            CheckView(sortedKeyValue);

            using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
            for (i = 0; i < entryCount; i++)
            {
                _db[[i, i, i]] = [(byte)(i + 1), (byte)(i + 1), (byte)(i + 1)];
            }

            CheckView((ISortedKeyValueStore)snapshot);
        }

        [Test]
        public void Can_GetMetric_AfterDispose()
        {
            _db.Dispose();
            Assert.That(_db.GatherMetric().Size, Is.EqualTo(0));
        }

        private void AssertCanGetViaAllMethod(IReadOnlyKeyValueStore kv, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            Assert.That(kv[key], Is.EqualTo(value.ToArray()));
            Assert.That(kv.KeyExists(key), Is.True);

            ReadFlags[] flags = [ReadFlags.None, ReadFlags.HintReadAhead, ReadFlags.HintCacheMiss];
            Span<byte> outBuffer = stackalloc byte[value.Length];
            foreach (ReadFlags flag in flags)
            {
                Assert.That(kv.Get(key, flags: flag), Is.EqualTo(value.ToArray()));

                Span<byte> buffer = kv.GetSpan(key, flag);
                Assert.That(buffer.ToArray(), Is.EqualTo(value.ToArray()));
                kv.DangerousReleaseMemory(buffer);

                int length = kv.Get(key, outBuffer);
                Assert.That(outBuffer[..length].ToArray(), Is.EqualTo(value.ToArray()));
            }

            using ISortedView iterator = ((ISortedKeyValueStore)kv).GetViewBetween(key, CreateNextKey(key));
            if (iterator.MoveNext())
            {
                Assert.That(iterator.CurrentKey.ToArray(), Is.EqualTo(key.ToArray()));
                Assert.That(iterator.CurrentValue.ToArray(), Is.EqualTo(value.ToArray()));
            }

            Assert.That(iterator.MoveNext(), Is.False);

            // Ai generated
            static byte[] CreateNextKey(ReadOnlySpan<byte> key)
            {
                // 1. Create a copy of the key to modify
                byte[] nextKey = key.ToArray();

                // 2. Iterate backwards (from the last byte to the first)
                for (int i = nextKey.Length - 1; i >= 0; i--)
                {
                    // If the byte is NOT 0xFF (255), we can just increment it and we are done.
                    if (nextKey[i] < 0xFF)
                    {
                        nextKey[i]++;
                        return nextKey;
                    }

                    // If the byte IS 0xFF, it rolls over to 0x00, and we "carry" the 1 to the next byte loop.
                    nextKey[i] = 0x00;
                }

                // 3. Handle Overflow (Edge Case: All bytes were 0xFF)
                // If we are here, the key was something like [FF, FF, FF].
                // The loop turned it into [00, 00, 00].
                // The "Next" lexicographical key is mathematically [01, 00, 00, 00].

                // Resize array to fit the new leading '1'
                byte[] overflowKey = new byte[nextKey.Length + 1];
                overflowKey[0] = 1;
                // The rest are already 0 from default initialization, so we return.
                return overflowKey;
            }
        }
    }
}
