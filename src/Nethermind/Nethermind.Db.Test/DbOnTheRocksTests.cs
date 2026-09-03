// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using NSubstitute;
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
        public void WriteOptions_is_correct()
        {
            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            WriteOptions options = db.WriteFlagsToWriteOptions(WriteFlags.LowPriority)!;
            Assert.That(options.GetLowPriority(), Is.True);
            Assert.That(options.GetDisableWal(), Is.False);

            options = db.WriteFlagsToWriteOptions(WriteFlags.LowPriority | WriteFlags.DisableWAL)!;
            Assert.That(options.GetLowPriority(), Is.True);
            Assert.That(options.GetDisableWal(), Is.True);

            options = db.WriteFlagsToWriteOptions(WriteFlags.DisableWAL)!;
            Assert.That(options.GetLowPriority(), Is.False);
            Assert.That(options.GetDisableWal(), Is.True);
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

        [TestCase("compaction_pri=kByCompensatedSize", true, TestName = "CanOpenWithAdditionalConfig_SingleOption")]
        [TestCase("compaction_pri=kByCompensatedSize;num_levels=4", true, TestName = "CanOpenWithAdditionalConfig_MultipleOptions")]
        [TestCase("compaction_pri=kSomethingElse", false, TestName = "CanOpenWithAdditionalConfig_InvalidOption")]
        public void CanOpenWithAdditionalConfig(string opts, bool success)
        {
            IDbConfig config = new DbConfig();
            config.AdditionalRocksDbOptions = opts;

            Action act = () =>
            {
                RocksDbConfigFactory configFactory = new(config, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
                using DbOnTheRocks _ = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, configFactory, LimboLogs.Instance);
            };

            if (success)
            {
                Assert.That(act, Throws.Nothing);
            }
            else
            {
                Assert.That(act, Throws.InstanceOf<RocksDbException>());
            }
        }

        [Test]
        public void SharedCacheCanBeCreatedAndDisposed()
        {
            HyperClockCacheWrapper cache = new((ulong)10.KiB);

            Assert.That(cache.Handle, Is.Not.Zero);
            Assert.That(() => cache.GetUsage(), Throws.Nothing);

            cache.Dispose();
            // Disposal must stay exactly-once so the GC memory pressure accounting cannot go negative.
            cache.Dispose();
        }

        [Test]
        // rocksdb aborts the process on a zero capacity, so this must fail as a configuration error.
        public void SharedCacheRejectsZeroCapacity() =>
            Assert.That(() => new HyperClockCacheWrapper(0), Throws.TypeOf<InvalidConfigurationException>());

        [TestCase(true)]
        [TestCase(false)]
        public void UseSharedCacheIfNoCacheIsSpecified(bool explicitCache)
        {
            if (Directory.Exists(DbPath)) Directory.Delete(DbPath, true);
            long sharedCacheSize = 10.KiB;

            using HyperClockCacheWrapper cache = new((ulong)sharedCacheSize);
            _dbConfig.BlocksDbRocksDbOptions = "block_based_table_factory.block_size=512;block_based_table_factory.prepopulate_block_cache=kFlushOnly;";
            if (explicitCache)
            {
                _dbConfig.BlocksDbRocksDbOptions += "block_based_table_factory.block_cache=1000000;";
            }

            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, DbNames.Blocks), _dbConfig,
                _rocksdbConfigFactory, LimboLogs.Instance, sharedCache: cache.Handle);

            Random rng = new();
            byte[] buffer = new byte[1024];
            for (int i = 0; i < 100; i++)
            {
                Hash256 someKey = Keccak.Compute(i.ToBigEndianByteArray());
                rng.NextBytes(buffer);
                db.PutSpan(someKey.Bytes, buffer, WriteFlags.None);
            }
            db.Flush();

            if (explicitCache)
            {
                Assert.That(db.GatherMetric().CacheSize, Is.GreaterThan(sharedCacheSize));
            }
            else
            {
                Assert.That(db.GatherMetric().CacheSize, Is.LessThan(sharedCacheSize));
            }
        }

        [Test]
        public void UseExplicitlyGivenCache()
        {
            _dbConfig.BlocksDbRocksDbOptions = "block_based_table_factory.block_size=512;block_based_table_factory.prepopulate_block_cache=kFlushOnly;";

            long cacheSize = 10.KiB;
            using HyperClockCacheWrapper cache = new((ulong)cacheSize);

            IRocksDbConfigFactory rocksDbConfigFactory = Substitute.For<IRocksDbConfigFactory>();
            rocksDbConfigFactory.GetForDatabase(Arg.Any<string>(), Arg.Any<string?>())
                .Returns<IRocksDbConfig>((c) =>
                {
                    string? arg1 = (string?)c[0];
                    string? arg2 = (string?)c[1];

                    IRocksDbConfig baseConfig = _rocksdbConfigFactory.GetForDatabase(arg1, arg2);

                    baseConfig = new AdjustedRocksdbConfig(baseConfig,
                        "",
                        0,
                        cache.Handle);

                    return baseConfig;
                });

            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, DbNames.Blocks), _dbConfig,
                rocksDbConfigFactory, LimboLogs.Instance);

            Random rng = new();
            byte[] buffer = new byte[1024];
            for (int i = 0; i < 100; i++)
            {
                Hash256 someKey = Keccak.Compute(i.ToBigEndianByteArray());
                rng.NextBytes(buffer);
                db.PutSpan(someKey.Bytes, buffer, WriteFlags.None);
            }
            db.Flush();

            long metricCacheUsage = db.GatherMetric().CacheSize;
            long directCacheUsage = cache.GetUsage();

            Assert.That(metricCacheUsage, Is.GreaterThan(0));
            Assert.That(directCacheUsage, Is.GreaterThan(0));
            Assert.That(metricCacheUsage, Is.EqualTo(directCacheUsage).Within(4.KiB));
        }

        [Test]
        public void Corrupted_exception_on_open_writes_marker_and_shuts_down()
        {
            IDbConfig config = new DbConfig();

            IFile file = Substitute.For<IFile>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(file);

            bool exceptionThrown = false;
            bool didShutDown = false;
            try
            {
                _ = new CorruptedDbOnTheRocks("test", GetRocksDbSettings("test", "test"), config,
                    _rocksdbConfigFactory,
                    LimboLogs.Instance,
                    fileSystem: fileSystem,
                    onFatalShutdown: () => didShutDown = true);
            }
            catch (RocksDbException)
            {
                exceptionThrown = true;
            }

            Assert.That(exceptionThrown, Is.True);
            // Genuine "Corruption:" writes the marker (schedules repair on restart) and shuts down.
            file.Received().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            Assert.That(didShutDown, Is.True);
        }

        // An "IO error" (fd exhaustion, full disk, permissions) is not on-disk corruption, so it
        // must NOT write the marker (that would run the lossy repair on a healthy DB on restart),
        // but it must still fast-shut down so partial writes aren't built upon.
        [TestCase("IO error: While open a file for random read: /db/000123.sst: Too many open files")]
        [TestCase("IO error: No space left on device")]
        [TestCase("IO error: While fsync: /db/000123.sst: Permission denied")]
        public void Io_error_on_open_shuts_down_without_writing_marker(string exceptionMessage)
        {
            IDbConfig config = new DbConfig();

            IFile file = Substitute.For<IFile>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(file);

            bool exceptionThrown = false;
            bool didShutDown = false;
            try
            {
                _ = new CorruptedDbOnTheRocks("test", GetRocksDbSettings("test", "test"), config,
                    _rocksdbConfigFactory,
                    LimboLogs.Instance,
                    fileSystem: fileSystem,
                    openExceptionMessage: exceptionMessage,
                    onFatalShutdown: () => didShutDown = true);
            }
            catch (RocksDbException)
            {
                exceptionThrown = true;
            }

            Assert.That(exceptionThrown, Is.True);
            file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            Assert.That(didShutDown, Is.True);
        }

        [Test]
        public void If_marker_exists_on_open_then_repair_before_open()
        {
            IDbConfig config = new DbConfig();

            IFile file = Substitute.For<IFile>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(file);

            string markerFile = Path.Join(Path.GetTempPath(), "test", "test", "corrupt.marker");
            file.Exists(markerFile).Returns(true);

            bool didRepair = false;

            try
            {
                _ = new RepairTrackingDbOnTheRocks(Path.Join(Path.GetTempPath(), "test"), GetRocksDbSettings("test", "test"), config, _rocksdbConfigFactory,
                    LimboLogs.Instance,
                    fileSystem: fileSystem,
                    onRepair: () => didRepair = true);
            }
            catch (Exception)
            {
            }

            Assert.That(didRepair, Is.True);
            file.Received().Delete(markerFile);
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

        [Test]
        public void RemoveRange_RemovesTheRangeAndNothingTouchingIt()
        {
            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            for (byte i = 0; i < 10; i++)
            {
                db.PutSpan([i], [i], WriteFlags.None);
            }

            db.RemoveRange([3], [7]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetValue(db, [2]), Is.EqualTo(new byte[] { 2 }), "the key below the lower bound must survive");
                Assert.That(GetValue(db, [3]), Is.Null, "the lower bound is inclusive");
                Assert.That(GetValue(db, [6]), Is.Null);
                Assert.That(GetValue(db, [7]), Is.EqualTo(new byte[] { 7 }),
                    "the upper bound is EXCLUSIVE - this is the block a pruning node still promises to serve");
                Assert.That(GetValue(db, [8]), Is.EqualTo(new byte[] { 8 }));
            }
        }

        [Test]
        public void RemoveRange_OnAnEmptyRange_RemovesNothing()
        {
            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            db.PutSpan([5], [5], WriteFlags.None);
            db.RemoveRange([5], [5]);

            Assert.That(GetValue(db, [5]), Is.EqualTo(new byte[] { 5 }),
                "first == last is an empty half-open range and must be a no-op, not a one-key delete");
        }

        [Test]
        public void RemoveRange_SurvivesReopen()
        {
            IDbConfig config = new DbConfig();
            using (DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance))
            {
                for (byte i = 0; i < 6; i++)
                {
                    db.PutSpan([i], [i], WriteFlags.None);
                }

                db.RemoveRange([1], [4]);
            }

            using DbOnTheRocks reopened = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetValue(reopened, [0]), Is.EqualTo(new byte[] { 0 }));
                Assert.That(GetValue(reopened, [1]), Is.Null, "the tombstone has to reach the WAL, or a restart resurrects a range the node already stopped announcing");
                Assert.That(GetValue(reopened, [3]), Is.Null);
                Assert.That(GetValue(reopened, [4]), Is.EqualTo(new byte[] { 4 }));
            }
        }

        [Test]
        public void RemoveRange_OnBlockNumberPrefixedKeys_TakesEveryHashAtEveryHeightInRange()
        {
            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            for (ulong number = 1; number <= 5; number++)
            {
                foreach (byte tag in new byte[] { 0xAA, 0xBB })
                {
                    db.PutSpan(BlockKey(number, tag), [tag], WriteFlags.None);
                }
            }

            db.RemoveRange(BlockKey(2, 0x00), BlockKey(4, 0x00));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetValue(db, BlockKey(1, 0xAA)), Is.Not.Null);
                Assert.That(GetValue(db, BlockKey(2, 0xAA)), Is.Null);
                Assert.That(GetValue(db, BlockKey(2, 0xBB)), Is.Null, "both hashes at a covered height must go, orphans included");
                Assert.That(GetValue(db, BlockKey(3, 0xBB)), Is.Null);
                Assert.That(GetValue(db, BlockKey(4, 0xAA)), Is.Not.Null, "the first retained height must be untouched");
                Assert.That(GetValue(db, BlockKey(5, 0xBB)), Is.Not.Null);
            }
        }

        [Test]
        public void ReclaimRange_GivesBackTheDiskTheRemovedRangeStillHolds()
        {
            // Auto-compaction off so each flush stays its own file, as a real block-numbered bottom level is.
            IDbConfig config = new DbConfig { AdditionalRocksDbOptions = "disable_auto_compactions=true;" };
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            byte[] value = new byte[4096];
            for (ulong number = 1; number <= 8; number++)
            {
                for (byte tag = 0; tag < 32; tag++)
                {
                    db.PutSpan(BlockKey(number, tag), value, WriteFlags.None);
                }

                db.Flush();
            }

            long before = SstBytes(DbPath);
            Assert.That(before, Is.GreaterThan(0), "the data has to be in SST files for there to be anything to give back");

            db.RemoveRange(BlockKey(1, 0x00), BlockKey(5, 0x00));
            long afterRemove = SstBytes(DbPath);

            db.ReclaimRange(BlockKey(1, 0x00), BlockKey(5, 0x00));
            long afterReclaim = SstBytes(DbPath);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(afterRemove, Is.GreaterThanOrEqualTo(before),
                    "a range tombstone is a write: on its own it frees nothing, which is the whole reason this method exists");
                Assert.That(afterReclaim, Is.LessThan(before * 2 / 3),
                    "half the heights were reclaimed, so the disk has to come back now rather than whenever compaction next happens to run");
                Assert.That(GetValue(db, BlockKey(5, 0x00)), Is.Not.Null,
                    "the first retained height must survive: the upper bound is exclusive for the unlink too");
                Assert.That(GetValue(db, BlockKey(8, 31)), Is.Not.Null);
                Assert.That(GetValue(db, BlockKey(1, 0x00)), Is.Null);
            }
        }

        [Test]
        public void ReclaimRange_WithAnExclusiveBoundEndingInZero_StillGivesTheDiskBack()
        {
            // The bound is lowered before the inclusive native call, and lowering it by truncating trailing zeroes
            // rather than borrowing through them drops it below every key sharing the removed bytes. At a bound of
            // 512 that is all 256 heights below it - a pass that publishes a boundary and returns nothing.
            IDbConfig config = new DbConfig { AdditionalRocksDbOptions = "disable_auto_compactions=true;" };
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            byte[] value = new byte[4096];
            for (ulong number = 256; number < 512; number++)
            {
                db.PutSpan(BlockKey(number, 0xAA), value, WriteFlags.None);
                db.Flush();
            }

            long before = SstBytes(DbPath);
            Assert.That(before, Is.GreaterThan(0), "the data has to be in SST files for there to be anything to give back");

            db.RemoveRange(BlockKey(256, 0x00), BlockKey(512, 0x00));
            db.ReclaimRange(BlockKey(256, 0x00), BlockKey(512, 0x00));

            Assert.That(SstBytes(DbPath), Is.LessThan(before / 4),
                "every height covered sits below a bound ending in a zero byte, so truncating instead of borrowing keeps all of them");
        }

        [Test]
        public void ReclaimRange_LeavesAKeySittingOnTheExclusiveBound()
        {
            // The native call's include_end would reach a file whose largest key is the exclusive bound itself, so
            // this pins the half-open contract for an arbitrary key rather than for the block-numbered callers, whose
            // exclusive bound happens to be unreachable.
            // Small target files and an explicit compaction so the two keys land in separate files. One flush puts
            // both in the same SST, and a file straddling the bound is never entirely inside the range - the test
            // would then pass without exercising the bound at all.
            IDbConfig config = new DbConfig { AdditionalRocksDbOptions = "target_file_size_base=1024;" };
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            byte[] value = new byte[4096];
            byte[] bound = [0x02];
            db.PutSpan([0x01], value, WriteFlags.None);
            db.PutSpan(bound, value, WriteFlags.None);
            db.Flush();
            db.Compact();

            db.RemoveRange([0x01], bound);
            db.ReclaimRange([0x01], bound);

            Assert.That(GetValue(db, bound), Is.Not.Null,
                "the exclusive bound is not in the range, so neither the tombstone nor the unlink may take it");
        }

        private static long SstBytes(string dbPath) => Directory
            .EnumerateFiles(dbPath, "*.sst", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

        private static byte[] BlockKey(ulong blockNumber, byte hashTag)
        {
            byte[] key = new byte[40];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, new ValueHash256(), key);
            key[8] = hashTag;
            return key;
        }

        // The column-family overload, which is the one every production receipt reclaim takes.
        [Test]
        public void RemoveRange_OnAColumn_HoldsTheBoundsAndLeavesOtherColumnsAlone()
        {
            IDbConfig config = new DbConfig();
            using ColumnsDb<ReceiptsColumns> columnsDb = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config,
                _rocksdbConfigFactory, LimboLogs.Instance,
                new List<ReceiptsColumns> { ReceiptsColumns.Blocks, ReceiptsColumns.Transactions });

            IDb target = columnsDb.GetColumnDb(ReceiptsColumns.Blocks);
            IDb bystander = columnsDb.GetColumnDb(ReceiptsColumns.Transactions);

            for (ulong number = 1; number <= 5; number++)
            {
                target.PutSpan(BlockKey(number, 0xAA), [(byte)number], WriteFlags.None);
                bystander.PutSpan(BlockKey(number, 0xAA), [(byte)number], WriteFlags.None);
            }

            ((IRangeRemovableKeyValueStore)target).RemoveRange(BlockKey(2, 0x00), BlockKey(4, 0x00));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(target.Get(BlockKey(1, 0xAA)), Is.Not.Null);
                Assert.That(target.Get(BlockKey(2, 0xAA)), Is.Null);
                Assert.That(target.Get(BlockKey(3, 0xAA)), Is.Null);
                Assert.That(target.Get(BlockKey(4, 0xAA)), Is.Not.Null, "the upper bound is exclusive on a column too");

                for (ulong number = 1; number <= 5; number++)
                {
                    Assert.That(bystander.Get(BlockKey(number, 0xAA)), Is.Not.Null,
                        $"height {number} of another column must be untouched - the tombstone has to be scoped to its column family");
                }
            }
        }

        private static byte[]? GetValue(DbOnTheRocks db, ReadOnlySpan<byte> key) => ((IReadOnlyKeyValueStore)db).Get(key);

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName) => new(dbName, dbPath)
        {
        };

        [Test]
        public void GetViewBetween_on_a_prefix_extractor_database_honours_a_bound_that_crosses_prefixes()
        {
            string dbPath = Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
            if (Directory.Exists(dbPath)) Directory.Delete(dbPath, true);
            Directory.CreateDirectory(dbPath);

            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(dbPath, GetRocksDbSettings(dbPath, "Code"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            for (int i = 0; i < 16; i++)
            {
                byte[] key = new byte[32];
                key[0] = (byte)i;
                key[1] = (byte)i;
                db.PutSpan(key, new byte[] { (byte)i }, WriteFlags.None);
            }

            db.Flush();

            // A one-byte lower bound and a 128-byte upper bound, so the two bounds fall in different capped:8
            // prefix buckets.
            byte[] upperBound = new byte[128];
            upperBound[0] = 0x0F;
            upperBound.AsSpan(1).Fill(0xFF);

            int seen = 0;
            using (ISortedView view = ((ISortedKeyValueStore)db).GetViewBetween([0x00], upperBound))
            {
                while (view.MoveNext()) seen++;
            }

            Assert.That(seen, Is.EqualTo(16),
                "every key from 0x00 to 0x0F is inside the requested range, so a prefix-configured database must still walk all of them rather than stopping inside the lower bound's prefix bucket");
        }

        [Test]
        public void GetViewBetween_on_a_prefix_extractor_database_returns_a_range_that_stays_inside_one_prefix()
        {
            string dbPath = Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
            if (Directory.Exists(dbPath)) Directory.Delete(dbPath, true);
            Directory.CreateDirectory(dbPath);

            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(dbPath, GetRocksDbSettings(dbPath, "Code"), config, _rocksdbConfigFactory, LimboLogs.Instance);

            for (int i = 0; i < 16; i++)
            {
                byte[] key = new byte[32];
                key[8] = (byte)i;
                db.PutSpan(key, new byte[] { (byte)i }, WriteFlags.None);
            }

            db.Flush();

            byte[] lowerBound = new byte[32];
            byte[] upperBound = new byte[32];
            upperBound[8] = 0x10;

            int seen = 0;
            using (ISortedView view = ((ISortedKeyValueStore)db).GetViewBetween(lowerBound, upperBound))
            {
                while (view.MoveNext()) seen++;
            }

            Assert.That(seen, Is.EqualTo(16),
                "the bounds share their capped:8 prefix, so this range keeps the prefix index and must still yield every key in it");
        }

        [TestCase(0, 0, ExpectedResult = false, TestName = "CrossesPrefixBucket_OnADatabaseWithoutAnExtractor_IsFalse")]
        [TestCase(8, 3, ExpectedResult = true, TestName = "CrossesPrefixBucket_OnBoundsShorterThanThePrefix_IsTrue")]
        [TestCase(8, 8, ExpectedResult = false, TestName = "CrossesPrefixBucket_OnBoundsSharingThePrefix_IsFalse")]
        public bool CrossesPrefixBucket_classifies_bounds(int prefixLength, int sharedBytes)
        {
            string dbPath = Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
            if (Directory.Exists(dbPath)) Directory.Delete(dbPath, true);
            Directory.CreateDirectory(dbPath);

            IDbConfig config = new DbConfig();
            string dbName = prefixLength == 0 ? "Blocks" : "Code";
            using DbOnTheRocks db = new(dbPath, GetRocksDbSettings(dbPath, dbName), config, _rocksdbConfigFactory, LimboLogs.Instance);

            byte[] first = new byte[sharedBytes == 3 ? 3 : 32];
            byte[] last = new byte[first.Length];
            if (first.Length > sharedBytes) last[sharedBytes] = 0xFF;

            return db.CrossesPrefixBucket(first, last);
        }
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

        private long AllocatedSpan
        {
            get
            {
                if (_db is ColumnDb columnDb)
                {
                    return columnDb._mainDb._allocatedSpan.Sum;
                }

                return (_db as DbOnTheRocks)._allocatedSpan.Sum;
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

        [TestCase(1)]
        [TestCase(1024)]
        [TestCase(8192)]
        public void Smoke_test_value_sizes(int valueSize)
        {
            byte[] value = new byte[valueSize];
            new Random(valueSize).NextBytes(value);

            _db[[1, 2, 3]] = value;
            AssertCanGetViaAllMethod(_db, [1, 2, 3], value);
        }

        [Test]
        public void Missing_value_uses_existing_get_semantics()
        {
            byte[] output = [0xA5, 0xA5, 0xA5];
            byte[]? value = _db.Get([1, 2, 3]);
            int length = _db.Get([1, 2, 3], output);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(value, Is.Null);
                Assert.That(length, Is.Zero);
                Assert.That(output, Is.EqualTo(new byte[] { 0xA5, 0xA5, 0xA5 }));
            }
        }

        [TestCase(0)]
        [TestCase(3)]
        public void C_style_get_rejects_undersized_output_without_modifying_it(int outputSize)
        {
            byte[] key = [1, 2, 3];
            _db[key] = [4, 5, 6, 7];
            byte[] output = new byte[outputSize];
            Array.Fill(output, (byte)0xA5);
            byte[] expectedOutput = (byte[])output.Clone();

            Assert.That(() => _db.Get(key, output), Throws.ArgumentException);
            Assert.That(output, Is.EqualTo(expectedOutput));
        }

        [Test]
        public void Empty_value_round_trips_without_modifying_output()
        {
            byte[] key = [1, 2, 3];
            _db[key] = [];
            byte[] output = [0xA5];
            byte[]? value = _db.Get(key);
            int length = _db.Get(key, output);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(value, Is.Empty);
                Assert.That(length, Is.Zero);
                Assert.That(output, Is.EqualTo(new byte[] { 0xA5 }));
            }
        }

        [Test(Description = "Different kind of ceiling seeks using pooled iterators on a mutable db")]
        public void TryGetCeiling_sees_writes_made_after_the_pooled_iterator_was_created([Values] bool midFlush, [Values] bool postFlush)
        {
            const int keyCount = 50, flushCount = 5;

            ISortedKeyValueStore sorted = (ISortedKeyValueStore)_db;

            // Odd suffixes only, so every even one is a gap a seek has to walk over, shuffled so about half the seeks go backwards
            byte[] suffixes = [.. Enumerable.Range(0, keyCount).Select(static i => (byte)(2 * i + 1))];
            Random rng = new(42);
            rng.Shuffle(suffixes);

            // Spreads the writes over that many L0 files rather than one, so a seek can trim the files it misses
            int flushEvery = Math.Max(1, keyCount / flushCount);

            // Keep updating the DB and checking that iterator sees the latest value
            for (int written = 0; written < suffixes.Length; written++)
            {
                byte i = suffixes[written];
                _db[[1, i]] = [i];
                AssertSeesLatest(i);

                if (midFlush && written % flushEvery == flushEvery - 1) _db.Flush();
            }

            if (postFlush) _db.Flush();

            foreach (byte i in suffixes)
                AssertSeesLatest(i);

            const byte pastLast = 2 * keyCount + 1;
            AssertFindsNothing([1, pastLast], [1, pastLast + 1], "past the last key");

            void AssertSeesLatest(byte i)
            {
                byte below = (byte)(i - 1);
                byte above = (byte)(i + 1);

                AssertFinds(i, [1, i], [1, above], "exact hit");
                AssertFinds(i, [1, below], [1, above], "ceiling walk");
                AssertFindsNothing([1, below], [1, i], $"gap below {i}");
            }

            void AssertFinds(byte i, ReadOnlySpan<byte> lowerBoundIncl, ReadOnlySpan<byte> upperBoundExcl, string because)
            {
                Span<byte> key = stackalloc byte[2];
                Span<byte> value = stackalloc byte[1];

                bool found = sorted.TryGetCeiling(lowerBoundIncl, upperBoundExcl, key, out int keyLength, value, out int valueLength);

                Assert.That(found, Is.True, $"write {i} must be visible to the pooled iterator ({because})");
                Assert.That(key[..keyLength].ToArray(), Is.EqualTo([1, i]), $"key of {i} ({because})");
                Assert.That(value[..valueLength].ToArray(), Is.EqualTo([i]), $"value of {i} ({because})");
            }

            void AssertFindsNothing(ReadOnlySpan<byte> lowerBoundIncl, ReadOnlySpan<byte> upperBoundExcl, string because)
            {
                Span<byte> key = stackalloc byte[2];
                Span<byte> value = stackalloc byte[1];

                bool found = sorted.TryGetCeiling(lowerBoundIncl, upperBoundExcl, key, out _, value, out _);
                Assert.That(found, Is.False, $"the pooled iterator must not report a stale hit ({because})");
            }
        }

        [Test]
        public void Can_read_back_empty_value()
        {
            byte[] key = [1, 2, 3];
            _db.Set(key, []);

            Assert.That(_db.KeyExists(key), Is.True);
            Assert.That(_db.Get(key), Is.Empty);
            Assert.That(_db.Get(key, []), Is.Zero);

            Span<byte> span = _db.GetSpan(key);
            Assert.That(span.IsEmpty, Is.True);
            _db.DangerousReleaseMemory(span);

            if (_db is IReadOnlyNativeKeyValueStore nativeStore)
            {
                ReadOnlySpan<byte> slice = nativeStore.GetNativeSlice(key, out nint handle);
                Assert.That(slice.IsEmpty, Is.True);
                nativeStore.DangerousReleaseHandle(handle);
            }

            Assert.That(AllocatedSpan, Is.Zero);
        }

        [Test]
        public void Get_into_output_buffer_reports_missing_key_and_rejects_undersized_buffer()
        {
            byte[] key = [1, 2, 3];
            _db.Set(key, [4, 5, 6]);

            Assert.That(_db.Get([9, 9, 9], new byte[3]), Is.Zero);
            Assert.That(() => _db.Get(key, new byte[2]), Throws.ArgumentException);
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
        public void SnapshotDisposeCleansUp()
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

            Assert.That(AllocatedSpan, Is.EqualTo(1));
            _db.DangerousReleaseMemory(readSpan);
            Assert.That(AllocatedSpan, Is.EqualTo(0));
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

            Assert.That(AllocatedSpan, Is.EqualTo(1));
            manager.Dispose();
            Assert.That(AllocatedSpan, Is.EqualTo(0));
        }

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName) => new(dbName, dbPath)
        {
        };

        [Test]
        public void Can_get_all_on_empty() => Assert.That(_db.GetAll(), Is.Empty);

        [Test]
        public void Smoke_test_iterator()
        {
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };

            KeyValuePair<byte[], byte[]>[] allValues = _db.GetAll().ToArray()!;
            Assert.That(allValues[0].Key, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(allValues[0].Value, Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        [Test]
        public void Full_enumerations_can_be_repeated_across_batches()
        {
            (byte[][] expectedKeys, byte[][] expectedValues) = SeedFullEnumerationBatches();
            IEnumerable<KeyValuePair<byte[], byte[]>> all = _db.GetAll(ordered: true);
            IEnumerable<byte[]> keys = _db.GetAllKeys(ordered: true);
            IEnumerable<byte[]> values = _db.GetAllValues(ordered: true);

            _ = all.Take(1).Single();
            _ = keys.Take(1).Single();
            _ = values.Take(1).Single();

            KeyValuePair<byte[], byte[]>[] firstAll = all.ToArray();
            KeyValuePair<byte[], byte[]>[] secondAll = all.ToArray();
            byte[][] firstKeys = keys.ToArray();
            byte[][] secondKeys = keys.ToArray();
            byte[][] firstValues = values.ToArray();
            byte[][] secondValues = values.ToArray();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstAll.Select(static item => item.Key), Is.EqualTo(expectedKeys));
                Assert.That(firstAll.Select(static item => item.Value), Is.EqualTo(expectedValues));
                Assert.That(secondAll.Select(static item => item.Key), Is.EqualTo(expectedKeys));
                Assert.That(secondAll.Select(static item => item.Value), Is.EqualTo(expectedValues));
                Assert.That(firstKeys, Is.EqualTo(expectedKeys));
                Assert.That(secondKeys, Is.EqualTo(expectedKeys));
                Assert.That(firstValues, Is.EqualTo(expectedValues));
                Assert.That(secondValues, Is.EqualTo(expectedValues));
            }
        }

        [Test]
        public void Full_enumerations_resume_after_deleted_boundary()
        {
            (byte[][] expectedKeys, byte[][] expectedValues) = SeedFullEnumerationBatches();
            int boundaryIndex = DbOnTheRocks.FullEnumerationBatchSize - 1;

            AssertResumesAfterDeletedBoundary(
                _db.GetAll(ordered: true).Select(static item => item.Key),
                expectedKeys[boundaryIndex],
                expectedValues[boundaryIndex],
                expectedKeys[boundaryIndex + 1]);
            AssertResumesAfterDeletedBoundary(
                _db.GetAllKeys(ordered: true),
                expectedKeys[boundaryIndex],
                expectedValues[boundaryIndex],
                expectedKeys[boundaryIndex + 1]);
            AssertResumesAfterDeletedBoundary(
                _db.GetAllValues(ordered: true),
                expectedKeys[boundaryIndex],
                expectedValues[boundaryIndex],
                expectedValues[boundaryIndex + 1]);
        }

        private (byte[][] Keys, byte[][] Values) SeedFullEnumerationBatches()
        {
            int count = DbOnTheRocks.FullEnumerationBatchSize + 1;
            byte[][] keys = new byte[count][];
            byte[][] values = new byte[count][];

            using IWriteBatch batch = _db.StartWriteBatch();
            for (int i = 0; i < count; i++)
            {
                keys[i] = i.ToBigEndianByteArray();
                values[i] = (i + count).ToBigEndianByteArray();
                batch.Set(keys[i], values[i]);
            }

            return (keys, values);
        }

        private void AssertResumesAfterDeletedBoundary(
            IEnumerable<byte[]> items,
            byte[] boundaryKey,
            byte[] boundaryValue,
            byte[] expectedNext)
        {
            using IEnumerator<byte[]> enumerator = items.GetEnumerator();
            for (int i = 0; i < DbOnTheRocks.FullEnumerationBatchSize; i++)
            {
                if (!enumerator.MoveNext())
                {
                    Assert.Fail($"Enumeration stopped at item {i} before reaching the batch boundary.");
                }
            }

            _db.Remove(boundaryKey);
            try
            {
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(enumerator.Current, Is.EqualTo(expectedNext));
            }
            finally
            {
                _db.Set(boundaryKey, boundaryValue);
            }
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

        [Test]
        public void DeadWeight_AgainstARealDatabase_TheAggregatedPropertiesParseAndTheOpenRangeCompactionDigestsTombstones()
        {
            RocksDbConfigFactory configFactory = new(new DbConfig(), new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
            using DbOnTheRocks db = new("testDeadWeight", GetRocksDbSettings("testDeadWeight", "DeadWeightTest"), new DbConfig(), configFactory, LimboLogs.Instance);
            IDb store = db;
            byte[] value = new byte[64];
            for (int i = 0; i < 2000; i++) store[Keccak.Compute(i.ToBigEndianByteArray()).BytesToArray()] = value;
            db.Flush();
            for (int i = 0; i < 2000; i++) store.Remove(Keccak.Compute(i.ToBigEndianByteArray()).Bytes);
            db.Flush();

            string? aggregated = db.GatherProperty("rocksdb.aggregated-table-properties");

            Assert.That(DbOnTheRocks.ExceedsDeadWeight(aggregated, long.MaxValue.ToString(), 0.5), Is.True,
                "the real property string of the shipped RocksDB version must parse and report the tombstones");

            db.Compact();

            string? afterwards = db.GatherProperty("rocksdb.aggregated-table-properties");
            Assert.That(DbOnTheRocks.ExceedsDeadWeight(afterwards, long.MaxValue.ToString(), 0.5), Is.False,
                "the open-range compaction must digest the tombstones, after which the trigger stands down");
        }

        [TestCase("# entries=6250000000; # deletions=3050000000;", "1000000000000", 0.5, true, TestName = "DeadWeight_TombstonesShadowMostPuts_Compacts")]
        [TestCase("# entries=3300000000; # deletions=100000000;", "1000000000000", 0.5, false, TestName = "DeadWeight_MostlyLivePuts_Declines")]
        [TestCase("# entries=100; # deletions=100;", "1000000000000", 0.5, true, TestName = "DeadWeight_OnlyTombstonesLeft_Compacts")]
        [TestCase("# entries=0; # deletions=0;", "1000000000000", 0.5, false, TestName = "DeadWeight_EmptyStore_Declines")]
        [TestCase("# entries=6250000000; # deletions=3050000000;", "999999999", 0.5, false, TestName = "DeadWeight_SmallStore_Declines")]
        [TestCase(null, "1000000000000", 0.5, false, TestName = "DeadWeight_NoTableProperties_Declines")]
        [TestCase("# entries=garbage; # deletions=1;", "1000000000000", 0.5, false, TestName = "DeadWeight_UnparsableEntries_Declines")]
        [TestCase("# entries=6250000000; # deletions=3050000000;", null, 0.5, false, TestName = "DeadWeight_NoTotalSize_Declines")]
        public void ExceedsDeadWeight_DecidesFromTheAggregatedTombstoneCounts(string? aggregated, string? total, double ratio, bool expected) =>
            Assert.That(DbOnTheRocks.ExceedsDeadWeight(aggregated, total, ratio), Is.EqualTo(expected));
    }

    class CorruptedDbOnTheRocks(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IList<string>? columnFamilies = null,
        IFileSystem? fileSystem = null,
        string openExceptionMessage = "Corruption: test corruption",
        Action? onFatalShutdown = null
        ) : DbOnTheRocks(basePath, dbSettings, dbConfig, rocksDbConfigFactory, logManager, columnFamilies, fileSystem)
    {
        protected override RocksDb DoOpen(string path, (DbOptions Options, ColumnFamilies? Families) db) => throw new RocksDbException(openExceptionMessage);

        // The open path throws from the base constructor, so the caller never gets a reference to
        // observe FatalShutdown on; report it through the injected callback instead of exiting.
        protected override void FatalShutdown() => onFatalShutdown?.Invoke();
    }

    class RepairTrackingDbOnTheRocks(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IFileSystem fileSystem,
        Action onRepair
        ) : DbOnTheRocks(basePath, dbSettings, dbConfig, rocksDbConfigFactory, logManager, fileSystem: fileSystem)
    {
        protected override void RepairDb(DbOptions dbOptions, string path) => onRepair();
    }
}
