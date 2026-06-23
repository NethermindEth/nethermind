// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
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
using Snappier;
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
        public void Fresh_database_uses_bounded_initial_map_size()
        {
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(".", "Blocks"), _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance);

            db.Set([1], [2]);
            db.Flush();

            Assert.That(db.GatherMetric().Size, Is.LessThan(128.MiB));
        }

        [Test]
        public void Multiple_fresh_databases_use_bounded_initial_map_size()
        {
            List<DbOnTheRocks> dbs = [];
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    DbOnTheRocks db = new(DbPath, GetRocksDbSettings($"db-{i}", $"Blocks-{i}"), _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance);
                    db.Set([1], [2]);
                    db.Flush();
                    dbs.Add(db);
                }

                long totalSize = dbs.Sum(db => db.GatherMetric().Size);
                Assert.That(totalSize, Is.LessThan(1.GiB));
            }
            finally
            {
                foreach (DbOnTheRocks db in dbs)
                {
                    db.Dispose();
                }
            }
        }

        [Test]
        public void Mdbx_value_compression_round_trips_and_shrinks_compressible_values()
        {
            using MdbxValueCompression compression = new(enabled: true);
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            Assert.That(compression.TryEncode(value, out byte[]? stored, out int storedLength), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(storedLength, Is.LessThan(value.Length));
                Assert.That(compression.Decode(stored.AsSpan(0, storedLength)), Is.EqualTo(value));
            }
        }

        [Test]
        public void Mdbx_value_compression_skips_values_below_min_threshold()
        {
            using MdbxValueCompression compression = new(enabled: true, minValueLength: 256);
            byte[] value = Enumerable.Repeat((byte)0x42, 255).ToArray();

            Assert.That(compression.TryEncode(value, out _, out _), Is.False);
        }

        [Test]
        public void Mdbx_value_compression_writes_versioned_zstd_marker()
        {
            using MdbxValueCompression compression = new(enabled: true);
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            Assert.That(compression.TryEncode(value, out byte[]? stored, out int storedLength), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(stored[4], Is.EqualTo(2));
                Assert.That(stored[5], Is.EqualTo(2));
                Assert.That(BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(6)), Is.EqualTo(value.Length));
                Assert.That(compression.Decode(stored.AsSpan(0, storedLength)), Is.EqualTo(value));
            }
        }

        [Test]
        public void Mdbx_value_compression_reads_legacy_snappy_marker()
        {
            using MdbxValueCompression compression = new(enabled: true);
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();
            byte[] stored = GC.AllocateUninitializedArray<byte>(9 + Snappy.GetMaxCompressedLength(value.Length));
            stored[0] = 0xFF;
            stored[1] = (byte)'N';
            stored[2] = (byte)'M';
            stored[3] = (byte)'X';
            stored[4] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(stored.AsSpan(5), value.Length);
            int compressedLength = Snappy.Compress(value, stored.AsSpan(9));

            Assert.That(compression.Decode(stored.AsSpan(0, 9 + compressedLength)), Is.EqualTo(value));
        }

        [Test]
        public void Mdbx_value_compression_honors_no_compression_option()
        {
            DbConfig config = new() { AdditionalRocksDbOptions = "compression=kNoCompression;" };
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "Blocks", validate: false);
            using MdbxValueCompression compression = MdbxValueCompression.Create(
                rocksConfig,
                LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                DbPath);

            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            Assert.That(compression.TryEncode(value, out _, out _), Is.False);
        }

        [Test]
        public void Mdbx_value_compression_honors_min_value_length_override()
        {
            DbConfig config = new();
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "Blocks", validate: false);
            WithEnvironmentVariable("NETHERMIND_MDBX_COMPRESSION_MIN_BYTES", "4096", () =>
            {
                using MdbxValueCompression compression = MdbxValueCompression.Create(
                    rocksConfig,
                    LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                    DbPath);

                Assert.That(compression.MinValueLength, Is.EqualTo(4096));
                Assert.That(compression.TryEncode(Enumerable.Repeat((byte)0x42, 1024).ToArray(), out _, out _), Is.False);
            });
        }

        [Test]
        public void Mdbx_value_compression_skips_state_values_by_default()
        {
            DbConfig config = new();
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "State", validate: false);
            using MdbxValueCompression compression = MdbxValueCompression.Create(
                rocksConfig,
                LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                Path.Combine(DbPath, "mainnet", "state", "0"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(compression.MinValueLength, Is.EqualTo(int.MaxValue));
                Assert.That(compression.TryEncode(Enumerable.Repeat((byte)0x42, 4096).ToArray(), out _, out _), Is.False);
            }
        }

        [Test]
        public void Mdbx_value_compression_honors_state_min_value_length_override()
        {
            DbConfig config = new();
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "State", validate: false);
            WithEnvironmentVariable("NETHERMIND_MDBX_STATE_COMPRESSION_MIN_BYTES", "4096", () =>
            {
                using MdbxValueCompression compression = MdbxValueCompression.Create(
                    rocksConfig,
                    LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                    Path.Combine(DbPath, "mainnet", "state", "0"));

                Assert.That(compression.MinValueLength, Is.EqualTo(4096));
                Assert.That(compression.TryEncode(Enumerable.Repeat((byte)0x42, 4096).ToArray(), out _, out _), Is.True);
            });
        }

        [Test]
        public void Mdbx_value_compression_honors_global_min_value_length_override_for_state()
        {
            DbConfig config = new();
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "State", validate: false);
            WithEnvironmentVariable("NETHERMIND_MDBX_COMPRESSION_MIN_BYTES", "4096", () =>
            {
                using MdbxValueCompression compression = MdbxValueCompression.Create(
                    rocksConfig,
                    LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                    Path.Combine(DbPath, "mainnet", "state", "0"));

                Assert.That(compression.MinValueLength, Is.EqualTo(4096));
            });
        }

        [Test]
        public void Mdbx_value_compression_treats_terminal_state_path_as_state_db()
        {
            DbConfig config = new();
            IRocksDbConfig rocksConfig = new PerTableDbConfig(config, "State", validate: false);
            using MdbxValueCompression compression = MdbxValueCompression.Create(
                rocksConfig,
                LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>(),
                Path.Combine(DbPath, "mainnet", "state"));

            Assert.That(compression.MinValueLength, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void Mdbx_value_compression_treats_invalid_marker_as_raw()
        {
            using MdbxValueCompression compression = new(enabled: true);
            byte[] raw = [0xFF, (byte)'N', (byte)'M', (byte)'X', 1, 1, 0, 0, 0, 0];

            Assert.That(compression.Decode(raw), Is.EqualTo(raw));
        }

        [Test]
        public void Mdbx_value_compression_dispose_is_idempotent_after_context_creation()
        {
            MdbxValueCompression compression = new(enabled: true);
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            Assert.That(compression.TryEncode(value, out byte[]? stored, out int storedLength), Is.True);
            Assert.That(compression.Decode(stored.AsSpan(0, storedLength)), Is.EqualTo(value));

            Assert.DoesNotThrow(compression.Dispose);
            Assert.DoesNotThrow(compression.Dispose);
        }

        [Test]
        public void Can_read_raw_values_written_before_mdbx_value_compression()
        {
            DbSettings settings = GetRocksDbSettings(".", "Blocks");
            DbConfig rawConfig = new() { RocksDbOptions = "compression=kNoCompression;" };
            RocksDbConfigFactory rawFactory = new(rawConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), LimboLogs.Instance, validateConfig: false);
            byte[] key = [1, 2, 3];
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            using (DbOnTheRocks db = new(DbPath, settings, rawConfig, rawFactory, LimboLogs.Instance))
            {
                db.Set(key, value);
            }

            using (DbOnTheRocks db = new(DbPath, settings, _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance))
            {
                Assert.That(db.Get(key), Is.EqualTo(value));
            }
        }

        [Test]
        public void Mdbx_value_compression_escapes_raw_values_with_compression_marker()
        {
            using MdbxValueCompression compression = new(enabled: true, minValueLength: int.MaxValue);
            byte[] value = [0xFF, (byte)'N', (byte)'M', (byte)'X', 2, 2, 1, 2, 3, 4, 5];

            Assert.That(compression.TryEncode(value, out byte[]? stored, out int storedLength, out MdbxValueEncodingKind encodingKind), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(encodingKind, Is.EqualTo(MdbxValueEncodingKind.EscapedRaw));
                Assert.That(stored, Is.Not.Null);
                Assert.That(storedLength, Is.EqualTo(value.Length + 5));
                Assert.That(compression.Decode(stored!.AsSpan(0, storedLength)), Is.EqualTo(value));
            }
        }

        [Test]
        public void Mdbx_database_round_trips_raw_values_with_compression_marker()
        {
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(".", "Blocks"), _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance);
            byte[] key = [0x42];
            byte[] value = [0xFF, (byte)'N', (byte)'M', (byte)'X', 2, 2, 1, 2, 3, 4, 5];

            db.Set(key, value);

            Assert.That(db.Get(key), Is.EqualTo(value));
        }

        [TestCase("1024", 1024L)]
        [TestCase("1KiB", 1024L)]
        [TestCase("1.5MiB", 1572864L)]
        [TestCase("2GiB", 2147483648L)]
        [TestCase("3GB", 3000000000L)]
        public void Mdbx_tuning_size_parser_supports_expected_units(string value, long expected)
        {
            Assert.That(MdbxTuningOptions.TryParseSize(value, out long result), Is.True);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("abc")]
        [TestCase("-1")]
        [TestCase("1XB")]
        public void Mdbx_tuning_size_parser_rejects_invalid_values(string value) =>
            Assert.That(MdbxTuningOptions.TryParseSize(value, out _), Is.False);

        [TestCase(4096)]
        [TestCase(16384)]
        [TestCase(65536)]
        public void Mdbx_tuning_accepts_valid_page_size(int pageSize) =>
            WithEnvironmentVariable("NETHERMIND_MDBX_PAGE_SIZE", pageSize.ToString(), () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.PageSize, Is.EqualTo(pageSize));
            });

        [TestCase(0)]
        [TestCase(8191)]
        [TestCase(131072)]
        public void Mdbx_tuning_rejects_invalid_page_size(int pageSize) =>
            WithEnvironmentVariable("NETHERMIND_MDBX_PAGE_SIZE", pageSize.ToString(), () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.PageSize, Is.EqualTo(MdbxTuningOptions.DefaultPageSize));
            });

        [TestCase("true", true)]
        [TestCase("1", true)]
        [TestCase("on", true)]
        [TestCase("false", false)]
        [TestCase("0", false)]
        [TestCase("off", false)]
        public void Mdbx_tuning_bool_parser_supports_expected_values(string value, bool expected)
        {
            Assert.That(MdbxTuningOptions.TryParseBool(value, out bool result), Is.True);
            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("maybe")]
        public void Mdbx_tuning_bool_parser_rejects_invalid_values(string value) =>
            Assert.That(MdbxTuningOptions.TryParseBool(value, out _), Is.False);

        [Test]
        public void Mdbx_tuning_defaults_enable_write_path_optimizations()
        {
            MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.EnableWriteMap, Is.True);
                Assert.That(options.EnableCoalesce, Is.True);
                Assert.That(options.EnableBatchGrouping, Is.True);
                Assert.That(options.MaxBatchGroupOperations, Is.EqualTo(MdbxTuningOptions.DefaultMaxBatchGroupOperations));
                Assert.That(options.MaxBatchGroupBytes, Is.EqualTo(MdbxTuningOptions.DefaultMaxBatchGroupBytes));
                Assert.That(options.GrowthStep, Is.EqualTo(MdbxTuningOptions.DefaultGrowthStep));
                Assert.That(options.MaxReaders, Is.EqualTo(MdbxTuningOptions.DefaultMaxReaders));
                Assert.That(options.RpAugmentLimit, Is.EqualTo(MdbxTuningOptions.DefaultRpAugmentLimit));
                Assert.That(options.DirtyPagesReserveLimit, Is.Zero);
                Assert.That(options.TransactionDirtyPagesLimit, Is.Zero);
                Assert.That(options.TransactionDirtyPagesInitial, Is.Zero);
            }
        }

        [Test]
        public void Mdbx_tuning_accepts_mdbx_page_cache_overrides()
        {
            WithEnvironmentVariable("NETHERMIND_MDBX_RP_AUGMENT_LIMIT", "8192", () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.RpAugmentLimit, Is.EqualTo(8192));
            });

            WithEnvironmentVariable("NETHERMIND_MDBX_DIRTY_PAGES_RESERVE_LIMIT", "2048", () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.DirtyPagesReserveLimit, Is.EqualTo(2048));
            });

            WithEnvironmentVariable("NETHERMIND_MDBX_TXN_DIRTY_PAGES_LIMIT", "4096", () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.TransactionDirtyPagesLimit, Is.EqualTo(4096));
            });

            WithEnvironmentVariable("NETHERMIND_MDBX_TXN_DIRTY_PAGES_INITIAL", "1024", () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.TransactionDirtyPagesInitial, Is.EqualTo(1024));
            });

            WithEnvironmentVariable("NETHERMIND_MDBX_BATCH_GROUP_MAX_BYTES", "32MiB", () =>
            {
                MdbxTuningOptions options = MdbxTuningOptions.ReadFromEnvironment(LimboLogs.Instance.GetClassLogger<DbOnTheRocksTests>());

                Assert.That(options.MaxBatchGroupBytes, Is.EqualTo(32L << 20));
            });
        }

        [Test]
        public void Concurrent_write_batches_commit_all_values()
        {
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(".", "Blocks"), _dbConfig, _rocksdbConfigFactory, LimboLogs.Instance);

            Parallel.For(0, 16, batchIndex =>
            {
                using IWriteBatch batch = db.StartWriteBatch();
                for (int itemIndex = 0; itemIndex < 256; itemIndex++)
                {
                    int keyValue = batchIndex * 256 + itemIndex;
                    byte[] key = BitConverter.GetBytes(keyValue);
                    batch.Set(key, [unchecked((byte)batchIndex), unchecked((byte)itemIndex)]);
                }
            });

            for (int keyValue = 0; keyValue < 16 * 256; keyValue++)
            {
                byte[] key = BitConverter.GetBytes(keyValue);
                Assert.That(db.Get(key), Is.Not.Null);
            }
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

        private static void WithEnvironmentVariable(string name, string value, Action action)
        {
            string? previous = Environment.GetEnvironmentVariable(name);
            try
            {
                Environment.SetEnvironmentVariable(name, value);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
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
        public void Compressed_sized_value_round_trips_through_all_read_paths()
        {
            byte[] key = [1, 2, 3];
            byte[] value = Enumerable.Repeat((byte)0x42, 4096).ToArray();

            _db[key] = value;
            AssertCanGetViaAllMethod(_db, key, value);

            using IKeyValueStoreSnapshot snapshot = ((IKeyValueStoreWithSnapshot)_db).CreateSnapshot();
            AssertCanGetViaAllMethod(snapshot, key, value);
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
        public void Sorted_view_start_before_positions_on_previous_key()
        {
            _db[[1]] = [1];
            _db[[3]] = [3];
            _db[[5]] = [5];

            using ISortedView view = ((ISortedKeyValueStore)_db).GetViewBetween([0], [9]);

            Assert.That(view.StartBefore([4]), Is.True);
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 3 }));
            Assert.That(view.CurrentValue.ToArray(), Is.EqualTo(new byte[] { 3 }));
            Assert.That(view.MoveNext(), Is.True);
            Assert.That(view.CurrentKey.ToArray(), Is.EqualTo(new byte[] { 5 }));
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
