// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
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
    }
}
