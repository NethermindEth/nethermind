// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using RocksDbSharp;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class DbOnTheRocksTests
    {
        [Test]
        public void Smoke_test()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("blocks", GetRocksDbSettings("blocks", "Blocks"), config, LimboLogs.Instance);
            db[new byte[] { 1, 2, 3 }] = new byte[] { 4, 5, 6 };
            Assert.AreEqual(new byte[] { 4, 5, 6 }, db[new byte[] { 1, 2, 3 }]);
        }

        [Test]
        public void Smoke_test_span()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("blocks", GetRocksDbSettings("blocks", "Blocks"), config, LimboLogs.Instance);
            byte[] key = new byte[] { 1, 2, 3 };
            byte[] value = new byte[] { 4, 5, 6 };
            db.PutSpan(key, value);
            Span<byte> readSpan = db.GetSpan(key);
            Assert.AreEqual(new byte[] { 4, 5, 6 }, readSpan.ToArray());
            db.DangerousReleaseMemory(readSpan);
        }

        [Test]
        public void Can_get_all_on_empty()
        {
            IDbConfig config = new DbConfig();
            DbOnTheRocks db = new("testIterator", GetRocksDbSettings("testIterator", "TestIterator"), config, LimboLogs.Instance);
            try
            {
                // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                _ = db.GetAll().ToList();
            }
            finally
            {
                db.Clear();
                db.Dispose();
            }
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
            IBatch batch = db.StartBatch();
            db.Dispose();
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

        private static RocksDbSettings GetRocksDbSettings(string dbPath, string dbName)
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
        public void Test_columndb_put_and_get_span_correctly_store_value()
        {
            string path = Path.Join(Path.GetTempPath(), "test");
            Directory.CreateDirectory(path);
            try
            {
                IDbConfig config = new DbConfig();
                using ColumnsDb<ReceiptsColumns> columnDb = new(path, GetRocksDbSettings("blocks", "Blocks"), config,
                    LimboLogs.Instance, new List<ReceiptsColumns>() { ReceiptsColumns.Blocks });

                using IDbWithSpan db = columnDb.GetColumnDb(ReceiptsColumns.Blocks);

                ValueKeccak key = Keccak.Compute("something");
                ValueKeccak value = Keccak.Compute("something");

                db.KeyExists(key.Bytes).Should().BeFalse();
                db.PutSpan(key.Bytes, value.Bytes);
                db.KeyExists(key.Bytes).Should().BeTrue();
                Span<byte> data = db.GetSpan(key.Bytes);
                data.SequenceEqual(value.Bytes);
                db.DangerousReleaseMemory(data);
            }
            finally
            {
                Directory.Delete(path, true);
            }
        }
    }

    class CorruptedDbOnTheRocks : DbOnTheRocks
    {
        public CorruptedDbOnTheRocks(
            string basePath,
            RocksDbSettings rocksDbSettings,
            IDbConfig dbConfig,
            ILogManager logManager,
            ColumnFamilies? columnFamilies = null,
            RocksDbSharp.Native? rocksDbNative = null,
            IFileSystem? fileSystem = null
        ) : base(basePath, rocksDbSettings, dbConfig, logManager, columnFamilies, rocksDbNative, fileSystem)
        {
        }

        protected override RocksDb DoOpen(string path, (DbOptions Options, ColumnFamilies? Families) db)
        {
            throw new RocksDbSharpException("Corruption: test corruption");
        }
    }
}
