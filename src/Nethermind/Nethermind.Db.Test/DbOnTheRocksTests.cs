// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class DbOnTheRocksTests
    {
        string DbPath => "testdb/" + TestContext.CurrentContext.Test.Name;

        [SetUp]
        public void Setup()
        {
            Directory.CreateDirectory(DbPath);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(DbPath, true);
        }

        [Test]
        public void WriteOptions_is_correct()
        {
            IDbConfig config = new DbConfig();
            using DbOnTheRocks db = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, LimboLogs.Instance);

            WriteOptions? options = db.WriteFlagsToWriteOptions(WriteFlags.LowPriority);
            Native.Instance.rocksdb_writeoptions_get_low_pri(options.Handle).Should().BeTrue();
            Native.Instance.rocksdb_writeoptions_get_disable_WAL(options.Handle).Should().BeFalse();

            options = db.WriteFlagsToWriteOptions(WriteFlags.LowPriority | WriteFlags.DisableWAL);
            Native.Instance.rocksdb_writeoptions_get_low_pri(options.Handle).Should().BeTrue();
            Native.Instance.rocksdb_writeoptions_get_disable_WAL(options.Handle).Should().BeTrue();

            options = db.WriteFlagsToWriteOptions(WriteFlags.DisableWAL);
            Native.Instance.rocksdb_writeoptions_get_low_pri(options.Handle).Should().BeFalse();
            Native.Instance.rocksdb_writeoptions_get_disable_WAL(options.Handle).Should().BeTrue();
        }

        [Test]
        public void Throws_whenMaxWriteBufferNumIs0()
        {
            IDbConfig config = new DbConfig();
            DbSettings settings = new("Blocks", DbPath)
            {
                BlockCacheSize = (ulong)1.KiB(),
                CacheIndexAndFilterBlocks = false,
                WriteBufferNumber = 0,
                WriteBufferSize = (ulong)1.KiB()
            };

            Action act = () => new DbOnTheRocks(DbPath, settings, config, LimboLogs.Instance);
            act.Should().Throw<InvalidConfigurationException>();
        }

        [Test]
        public async Task Dispose_while_writing_does_not_cause_access_violation_exception()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("testDispose1", GetRocksDbSettings("testDispose1", "TestDispose1"), config, LimboLogs.Instance);

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

            firstWriteWait.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue();

            db.Dispose();

            await Task.Delay(100);

            cancelSource.Cancel();
            writeCompleted.Should().BeFalse();

            task.IsFaulted.Should().BeTrue();
            task.Dispose();
        }

        [Test]
        public void Dispose_wont_cause_ObjectDisposedException_when_batch_is_still_open()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("testDispose2", GetRocksDbSettings("testDispose2", "TestDispose2"), config, LimboLogs.Instance);
            IWriteBatch writeBatch = db.StartWriteBatch();
            db.Dispose();
        }

        [Test]
        public void CanOpenWithFileWarmer()
        {
            IDbConfig config = new DbConfig();
            config.EnableFileWarmer = true;
            {
                using DbOnTheRocks db = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, LimboLogs.Instance);
                for (int i = 0; i < 1000; i++)
                {
                    db[i.ToBigEndianByteArray()] = i.ToBigEndianByteArray();
                }
            }

            {
                using DbOnTheRocks db = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, LimboLogs.Instance);
            }
        }

        [TestCase("compaction_pri=kByCompensatedSize", true)]
        [TestCase("compaction_pri=kByCompensatedSize;num_levels=4", true)]
        [TestCase("compaction_pri=kSomethingElse", false)]
        public void CanOpenWithAdditionalConfig(string opts, bool success)
        {
            IDbConfig config = new DbConfig();
            config.AdditionalRocksDbOptions = opts;

            Action act = () =>
            {
                using DbOnTheRocks db = new("testFileWarmer", GetRocksDbSettings("testFileWarmer", "FileWarmerTest"), config, LimboLogs.Instance);
            };

            if (success)
            {
                act.Should().NotThrow();
            }
            else
            {
                act.Should().Throw<RocksDbException>();
            }
        }

        [Test]
        public void Corrupted_exception_on_open_would_create_marker()
        {
            IDbConfig config = new DbConfig();

            IFile file = Substitute.For<IFile>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(file);

            bool exceptionThrown = false;
            try
            {
                CorruptedDbOnTheRocks db = new("test", GetRocksDbSettings("test", "test"), config,
                    LimboLogs.Instance,
                    fileSystem: fileSystem);
            }
            catch (RocksDbSharpException)
            {
                exceptionThrown = true;
            }

            exceptionThrown.Should().BeTrue();
            file.Received().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
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

            RocksDbSharp.Native native = Substitute.For<RocksDbSharp.Native>();

            try
            {
                DbOnTheRocks db = new(Path.Join(Path.GetTempPath(), "test"), GetRocksDbSettings("test", "test"), config,
                    LimboLogs.Instance,
                    fileSystem: fileSystem,
                    rocksDbNative: native);
            }
            catch (Exception)
            {
            }

            native.Received().rocksdb_repair_db(Arg.Any<IntPtr>(), Arg.Any<string>(), out Arg.Any<IntPtr>());
            file.Received().Delete(markerFile);
        }

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName)
        {
            return new(dbName, dbPath)
            {
                BlockCacheSize = (ulong)1.KiB(),
                CacheIndexAndFilterBlocks = false,
                WriteBufferNumber = 4,
                WriteBufferSize = (ulong)1.KiB()
            };
        }
    }

    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.None)]
    public class DbOnTheRocksDbTests
    {
        string DbPath => "testdb/" + TestContext.CurrentContext.Test.Name;
        private IDb _db = null!;
        IDisposable? _dbDisposable = null!;

        private readonly bool _useColumnDb = false;

        public DbOnTheRocksDbTests(bool useColumnDb)
        {
            _useColumnDb = useColumnDb;
        }

        [SetUp]
        public void Setup()
        {
            if (Directory.Exists(DbPath))
            {
                Directory.Delete(DbPath, true);
            }

            Directory.CreateDirectory(DbPath);
            if (_useColumnDb)
            {
                IDbConfig config = new DbConfig();
                ColumnsDb<ReceiptsColumns> columnsDb = new(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config,
                    LimboLogs.Instance, new List<ReceiptsColumns>() { ReceiptsColumns.Blocks });
                _dbDisposable = columnsDb;

                _db = (ColumnDb)columnsDb.GetColumnDb(ReceiptsColumns.Blocks);
            }
            else
            {
                IDbConfig config = new DbConfig();
                _db = new DbOnTheRocks(DbPath, GetRocksDbSettings(DbPath, "Blocks"), config, LimboLogs.Instance);
                _dbDisposable = _db;
            }
        }

        private long AllocatedSpan
        {
            get
            {
                if (_db is ColumnDb columnDb)
                {
                    return columnDb._mainDb._allocatedSpan;
                }

                return (_db as DbOnTheRocks)._allocatedSpan;
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
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };
            Assert.That(_db[new byte[] { 1, 2, 3 }], Is.EqualTo(new byte[] { 4, 5, 6 }));

            _db.Set(new byte[] { 2, 3, 4 }, new byte[] { 5, 6, 7 }, WriteFlags.LowPriority);
            Assert.That(_db[new byte[] { 2, 3, 4 }], Is.EqualTo(new byte[] { 5, 6, 7 }));
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
                _db[i.ToBigEndianByteArray()].Should().BeEquivalentTo(i.ToBigEndianByteArray());
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

            AllocatedSpan.Should().Be(1);
            _db.DangerousReleaseMemory(readSpan);
            AllocatedSpan.Should().Be(0);
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

            AllocatedSpan.Should().Be(1);
            manager.Dispose();
            AllocatedSpan.Should().Be(0);
        }

        private static DbSettings GetRocksDbSettings(string dbPath, string dbName)
        {
            return new(dbName, dbPath)
            {
                BlockCacheSize = (ulong)1.KiB(),
                CacheIndexAndFilterBlocks = false,
                WriteBufferNumber = 4,
                WriteBufferSize = (ulong)1.KiB()
            };
        }

        [Test]
        public void Can_get_all_on_empty()
        {
            _ = _db.GetAll().ToList();
        }

        [Test]
        public void Smoke_test_iterator()
        {
            _db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };

            KeyValuePair<byte[], byte[]>[] allValues = _db.GetAll().ToArray()!;
            allValues[0].Key.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
            allValues[0].Value.Should().BeEquivalentTo(new byte[] { 4, 5, 6 });
        }

    }

    class CorruptedDbOnTheRocks : DbOnTheRocks
    {
        public CorruptedDbOnTheRocks(
            string basePath,
            DbSettings dbSettings,
            IDbConfig dbConfig,
            ILogManager logManager,
            IList<string>? columnFamilies = null,
            RocksDbSharp.Native? rocksDbNative = null,
            IFileSystem? fileSystem = null
        ) : base(basePath, dbSettings, dbConfig, logManager, columnFamilies, rocksDbNative, fileSystem)
        {
        }

        protected override RocksDb DoOpen(string path, (DbOptions Options, ColumnFamilies? Families) db)
        {
            throw new RocksDbSharpException("Corruption: test corruption");
        }
    }
}
