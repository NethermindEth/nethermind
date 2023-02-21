// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [TestFixture]
    public class MemoryHintManTests
    {
        private const long GB = 1000 * 1000 * 1000;
        private const long MB = 1000 * 1000;

        private IDbConfig _dbConfig;
        private ISyncConfig _syncConfig;
        private IInitConfig _initConfig;
        private ITxPoolConfig _txPoolConfig;
        private INetworkConfig _networkConfig;
        private MemoryHintMan _memoryHintMan;

        [SetUp]
        public void Setup()
        {
            _dbConfig = new DbConfig();
            _syncConfig = new SyncConfig();
            _initConfig = new InitConfig();
            _txPoolConfig = new TxPoolConfig();
            _networkConfig = new NetworkConfig();
            _memoryHintMan = new MemoryHintMan(LimboLogs.Instance);
        }

        private void SetMemoryAllowances(uint cpuCount)
        {
            _memoryHintMan.SetMemoryAllowances(
                _dbConfig,
                _initConfig,
                _networkConfig,
                _syncConfig,
                _txPoolConfig,
                cpuCount);
        }

        [TestCase(4 * GB, 2u, 4u, 11)]
        [TestCase(4 * GB, 4u, 8u, 11)]
        [TestCase(8 * GB, 1u, 2u, 11)]
        [TestCase(1 * GB, 4u, 8u, 11)]
        [TestCase(512 * MB, 4u, 8u, 10)]
        [TestCase(256 * MB, 6u, 12u, 8)]
        [TestCase(1000 * MB, 12u, 24u, 9)]
        [TestCase(2000 * MB, 12u, 24u, 10)]
        [TestCase(1000 * MB, 12u, 8u, 11)]
        [TestCase(2000 * MB, 12u, 8u, 11)]
        public void Netty_arena_order_is_configured_correctly(long memoryHint, uint cpuCount, uint maxArenaCount, int expectedArenaOrder)
        {
            _txPoolConfig.Size = 128;
            _initConfig.DiagnosticMode = DiagnosticMode.MemDb;
            _initConfig.MemoryHint = (long)memoryHint;
            _networkConfig.MaxNettyArenaCount = maxArenaCount;
            SetMemoryAllowances(cpuCount);
            _networkConfig.NettyArenaOrder.Should().Be(expectedArenaOrder);
        }

        [Test]
        public void Db_size_are_computed_correctly(
            [Values(256 * MB, 512 * MB, 1 * GB, 4 * GB, 6 * GB, 16 * GB, 32 * GB, 64 * GB, 128 * GB)]
            long memoryHint,
            [Values(1u, 2u, 3u, 4u, 8u, 32u)] uint cpuCount,
            [Values(true, false)] bool fastSync,
            [Values(true, false)] bool fastBlocks)
        {
            // OK to throw here
            if (memoryHint == 256.MB())
            {
                _txPoolConfig.Size = 128;
                _syncConfig.FastBlocks = false;
                _initConfig.DiagnosticMode = DiagnosticMode.MemDb;
            }

            _initConfig.MemoryHint = memoryHint;
            SetMemoryAllowances(cpuCount);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = fastSync;
            syncConfig.FastBlocks = fastBlocks;

            IDbConfig dbConfig = _dbConfig;

            ulong totalForHeaders = dbConfig.HeadersDbBlockCacheSize
                                    + dbConfig.HeadersDbWriteBufferNumber * dbConfig.HeadersDbWriteBufferSize;

            ulong totalForBlocks = dbConfig.BlocksDbBlockCacheSize
                                   + dbConfig.BlocksDbWriteBufferNumber * dbConfig.BlocksDbWriteBufferSize;

            ulong totalForInfos = dbConfig.BlockInfosDbBlockCacheSize
                                  + dbConfig.BlockInfosDbWriteBufferNumber * dbConfig.BlockInfosDbWriteBufferSize;

            ulong totalForReceipts = dbConfig.ReceiptsDbBlockCacheSize
                                     + dbConfig.ReceiptsDbWriteBufferNumber * dbConfig.ReceiptsDbWriteBufferSize;

            ulong totalForCode = dbConfig.CodeDbBlockCacheSize
                                 + dbConfig.CodeDbWriteBufferNumber * dbConfig.CodeDbWriteBufferSize;

            ulong totalForPending = dbConfig.PendingTxsDbBlockCacheSize
                                    + dbConfig.PendingTxsDbWriteBufferNumber * dbConfig.PendingTxsDbWriteBufferSize;

            ulong totalMem = (dbConfig.BlockCacheSize + dbConfig.WriteBufferNumber * dbConfig.WriteBufferSize)
                             + totalForHeaders
                             + totalForBlocks
                             + totalForInfos
                             + totalForReceipts
                             + totalForCode
                             + totalForPending;

            if (_initConfig.DiagnosticMode != DiagnosticMode.MemDb)
            {
                // some rounding differences are OK
                totalMem.Should().BeGreaterThan((ulong)((memoryHint - 200.MB()) * 0.6));
                totalMem.Should().BeLessThan((ulong)((memoryHint - 200.MB()) * 0.9));
            }
            else
            {
                _memoryHintMan.DbMemory.Should().BeGreaterThan((long)((memoryHint - 100.MB()) * 0.6));
                _memoryHintMan.DbMemory.Should().BeLessThan((long)((memoryHint - 100.MB()) * 0.9));
            }
        }

        [TestCase(100 * GB, 16u, -1)]
        [TestCase(100 * GB, 16u, 1)]
        [TestCase(384 * MB, 1u, -1)]
        [TestCase(384 * MB, 1u, 1)]
        public void Will_not_change_non_default_arena_order(long memoryHint, uint cpuCount, int differenceFromDefault)
        {
            _initConfig.MemoryHint = (long)memoryHint;
            int manuallyConfiguredArenaOrder = INetworkConfig.DefaultNettyArenaOrder + differenceFromDefault;
            _networkConfig.NettyArenaOrder = manuallyConfiguredArenaOrder;
            SetMemoryAllowances(cpuCount);
            _networkConfig.NettyArenaOrder.Should().Be(manuallyConfiguredArenaOrder);
        }

        [TestCase(4 * GB, 0u)]
        public void Incorrect_input_throws(long memoryHint, uint cpuCount)
        {
            _initConfig.MemoryHint = memoryHint;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SetMemoryAllowances(cpuCount));
        }

        [TestCase(500 * GB)]
        public void Big_value_at_memory_hint(long memoryHint)
        {
            _initConfig.MemoryHint = memoryHint;
            SetMemoryAllowances(1);
            Trie.MemoryAllowance.TrieNodeCacheCount.Should().BeGreaterThan(0);
        }
    }
}
