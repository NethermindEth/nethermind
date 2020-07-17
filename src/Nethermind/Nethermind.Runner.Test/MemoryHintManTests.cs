//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    [TestFixture]
    public class MemoryHintManTests
    {
        private const ulong GB = 1000 * 1000 * 1000;
        private const ulong MB = 1000 * 1000;

        private IDbConfig _dbConfig = new DbConfig();
        private ISyncConfig _syncConfig = new SyncConfig();
        private IInitConfig _initConfig = new InitConfig();
        private ITxPoolConfig _txPoolConfig = new TxPoolConfig();
        private INetworkConfig _networkConfig = new NetworkConfig();
        private MemoryHintMan _memoryHintMan = new MemoryHintMan(LimboLogs.Instance);

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
        
        [TestCase(4 * GB, 2u, 11)]
        [TestCase(4 * GB, 4u, 11)]
        [TestCase(8 * GB, 1u, 11)]
        [TestCase(1 * GB, 4u, 11)]
        [TestCase(512 * MB, 4u, 10)]
        [TestCase(256 * MB, 6u, 9)]
        [TestCase(1000 * MB, 12u, 10)]
        [TestCase(2000 * MB, 12u, 11)]
        public void Netty_arena_order_is_configured_correctly(ulong memoryHint, uint cpuCount, int expectedArenaOrder)
        {
            _initConfig.MemoryHint = (long)memoryHint;
            SetMemoryAllowances(cpuCount);
            _networkConfig.NettyArenaOrder.Should().Be(expectedArenaOrder);
        }

        [Test]
        public void Db_size_are_computed_correctly(
            [Values(256 * MB, 512 * MB, 1 * GB, 4 * GB, 6 * GB, 16 * GB, 32 * GB, 64 * GB, 128 * GB)]
            ulong memoryHint,
            [Values(1u, 2u, 3u, 4u, 8u, 32u)] uint cpuCount,
            [Values(true, false)] bool fastSync,
            [Values(true, false)] bool fastBlocks)
        {
            // OK to throw here
            if (memoryHint == 256.MB() && cpuCount >= 4u)
            {
                return;
            }
            
            _initConfig.MemoryHint = (long)memoryHint;
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

            // some rounding differences are OK
            totalMem.Should().BeGreaterThan((ulong) (memoryHint * 0.75m) - 2 * MB);
            totalMem.Should().BeLessThan((ulong) (memoryHint * 0.75m) + 2 * MB);
        }

        [TestCase(100 * GB, 16u, -1)]
        [TestCase(100 * GB, 16u, 1)]
        [TestCase(256 * MB, 1u, -1)]
        [TestCase(256 * MB, 1u, 1)]
        public void Will_not_change_non_default_arena_order(ulong memoryHint, uint cpuCount, int differenceFromDefault)
        {
            _initConfig.MemoryHint = (long)memoryHint;
            int manuallyConfiguredArenaOrder = INetworkConfig.DefaultNettyArenaOrder + differenceFromDefault;
            _networkConfig.NettyArenaOrder = manuallyConfiguredArenaOrder;
            SetMemoryAllowances(cpuCount);
            _networkConfig.NettyArenaOrder.Should().Be(manuallyConfiguredArenaOrder);
        }

        [TestCase(0 * GB, 16u)]
        [TestCase(4 * GB, 0u)]
        [TestCase(MemoryHintMan.MinMemoryHint - 1, 0u)]
        public void Incorrect_input_throws(ulong memoryHint, uint cpuCount)
        {
            _initConfig.MemoryHint = (long) memoryHint;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SetMemoryAllowances(cpuCount));
        }
    }
}