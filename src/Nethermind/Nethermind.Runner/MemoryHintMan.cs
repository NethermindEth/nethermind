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
using System.Diagnostics;
using System.IO;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.TxPool;

namespace Nethermind.Runner
{
    /// <summary>
    /// Applies changes to the NetworkConfig and the DbConfig so to adhere to the max memory limit hint. 
    /// </summary>
    public class MemoryHintMan
    {
        private ILogger _logger;

        public MemoryHintMan(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<MemoryHintMan>()
                      ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void SetMemoryAllowances(
            IDbConfig dbConfig,
            IInitConfig initConfig,
            INetworkConfig networkConfig,
            ISyncConfig syncConfig,
            ITxPoolConfig txPoolConfig,
            uint cpuCount)
        {
            TotalMemory = (ulong) (initConfig.MemoryHint ?? (long) 2.GB());
            ValidateCpuCount(cpuCount);

            checked
            {
                if (_logger.IsInfo) _logger.Info("Setting up memory allowances");
                if (_logger.IsInfo) _logger.Info($"  memory hint:        {TotalMemory / 1000 / 1000}MB");
                _remainingMemory = (ulong) (initConfig.MemoryHint ?? (long) 2.GB());
                _remainingMemory -= GeneralMemory;
                if (_logger.IsInfo) _logger.Info($"  general memory:     {GeneralMemory / 1000 / 1000}MB");
                AssignPeersMemory(networkConfig);
                _remainingMemory -= PeersMemory;
                if (_logger.IsInfo) _logger.Info($"  peers memory:       {PeersMemory / 1000 / 1000}MB");
                AssignNettyMemory(networkConfig, cpuCount);
                _remainingMemory -= NettyMemory;
                if (_logger.IsInfo) _logger.Info($"  Netty memory:       {NettyMemory / 1000 / 1000}MB");
                AssignTxPoolMemory(txPoolConfig);
                _remainingMemory -= TxPoolMemory;
                if (_logger.IsInfo) _logger.Info($"  mempool memory:     {TxPoolMemory / 1000 / 1000}MB");
                AssignFastBlocksMemory(syncConfig);
                _remainingMemory -= FastBlocksMemory;
                if (_logger.IsInfo) _logger.Info($"  fast blocks memory: {FastBlocksMemory / 1000 / 1000}MB");
                AssignTrieCacheMemory();
                _remainingMemory -= TrieCacheMemory;
                if (_logger.IsInfo) _logger.Info($"  trie memory:        {TrieCacheMemory / 1000 / 1000}MB");
                UpdateDbConfig(cpuCount, syncConfig, dbConfig, initConfig);
                _remainingMemory -= DbMemory;
                if (_logger.IsInfo) _logger.Info($"  DB memory:          {DbMemory / 1000 / 1000}MB");
            }
        }

        private ulong _remainingMemory;

        public ulong TotalMemory = 1024 * 1024 * 1024;
        public ulong GeneralMemory { get; } = 32.MB();
        public ulong FastBlocksMemory { get; private set; }
        public ulong DbMemory { get; private set; }
        public ulong NettyMemory { get; private set; }
        public ulong TxPoolMemory { get; private set; }
        public ulong PeersMemory { get; private set; }
        public ulong TrieCacheMemory { get; private set; }

        private void AssignTrieCacheMemory()
        {
            TrieCacheMemory = (ulong) (0.2 * _remainingMemory);
            Trie.MemoryAllowance.TrieNodeCacheMemory = (ulong) TrieCacheMemory;
        }

        private void AssignPeersMemory(INetworkConfig networkConfig)
        {
            PeersMemory = (ulong) networkConfig.ActivePeersMaxCount * 1.MB();
            if (PeersMemory > _remainingMemory * 0.75)
            {
                throw new InvalidDataException(
                    $"Memory hint is not enough to satisfy the {nameof(NetworkConfig)}.{nameof(INetworkConfig.ActivePeersMaxCount)}. " +
                    $"Assign at least MaxActivePeers * ~1MB * ~1.25 of memory.");
            }
        }

        private void AssignTxPoolMemory(ITxPoolConfig txPoolConfig)
        {
            ulong hashCacheMemory = (ulong) txPoolConfig.Size / 4UL * 1024UL * 128UL;
            if ((_remainingMemory * 0.05) < hashCacheMemory)
            {
                hashCacheMemory = (ulong) Math.Min((long) (_remainingMemory * 0.05), (long) (hashCacheMemory));
            }

            MemoryAllowance.TxHashCacheSize = (int) (hashCacheMemory / 128);
            hashCacheMemory = (ulong) (MemoryAllowance.TxHashCacheSize * 128);

            ulong txPoolMemory = (ulong) txPoolConfig.Size * 40.KB() + hashCacheMemory;
            if (txPoolMemory > _remainingMemory * 0.5)
            {
                throw new InvalidDataException(
                    $"Memory hint is not enough to satisfy the {nameof(TxPoolConfig)}.{nameof(TxPoolConfig.Size)}");
            }

            TxPoolMemory = txPoolMemory;
        }

        private void AssignFastBlocksMemory(ISyncConfig syncConfig)
        {
            if (syncConfig.FastBlocks)
            {
                if (!syncConfig.DownloadBodiesInFastSync && !syncConfig.DownloadReceiptsInFastSync)
                {
                    FastBlocksMemory = (ulong) Math.Min((long) 128.MB(), (long) (0.1 * _remainingMemory));
                }
                else
                {
                    FastBlocksMemory = (ulong) Math.Min((long) 1.GB(), (long) (0.1 * _remainingMemory));
                }

                Synchronization.MemoryAllowance.FastBlocksMemory = FastBlocksMemory;
            }
        }

        private void UpdateDbConfig(uint cpuCount, ISyncConfig syncConfig, IDbConfig dbConfig, IInitConfig initConfig)
        {
            if (initConfig.DiagnosticMode == DiagnosticMode.MemDb)
            {
                DbMemory = _remainingMemory;
                return;
            }

            DbMemory = _remainingMemory;
            ulong remaining = DbMemory;
            DbNeeds dbNeeds = GetHeaderNeeds(cpuCount, syncConfig);
            DbGets dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.HeadersDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.HeadersDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.HeadersDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetBlocksNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.BlocksDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.BlocksDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.BlocksDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetBlockInfosNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.BlockInfosDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.BlockInfosDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.BlockInfosDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetReceiptsNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.ReceiptsDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.ReceiptsDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.ReceiptsDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetCodeNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.CodeDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.CodeDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.CodeDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetPendingTxNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.PendingTxsDbWriteBufferNumber = dbGets.Buffers;
            dbConfig.PendingTxsDbWriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.PendingTxsDbBlockCacheSize = dbGets.CacheMem;

            dbNeeds = GetStateNeeds(cpuCount, syncConfig);
            dbGets = GiveItWhatYouCan(dbNeeds, DbMemory, remaining);
            remaining -= dbGets.CacheMem + dbGets.Buffers * dbGets.SingleBufferMem;
            dbConfig.WriteBufferNumber = dbGets.Buffers;
            dbConfig.WriteBufferSize = dbGets.SingleBufferMem;
            dbConfig.BlockCacheSize = dbGets.CacheMem;
        }

        private DbGets GiveItWhatYouCan(DbNeeds dbNeeds, ulong memoryHint, ulong remaining)
        {
            uint buffers = dbNeeds.PreferredBuffers; // this is fine for now
            decimal maxPercentage = Math.Min((decimal) remaining / memoryHint, dbNeeds.PreferredMemoryPercentage);
            ulong availableMemory = remaining;
            ulong minBufferMem = buffers * dbNeeds.PreferredMinBufferMemory;
            ulong minCacheMem = dbNeeds.PreferredMinMemory;
            ulong maxBufferMem = buffers * dbNeeds.PreferredMaxBufferMemory;
            ulong minMemory = minBufferMem + minCacheMem;
            if (minMemory > availableMemory)
            {
                throw new ArgumentException($"Memory hint of {TotalMemory} is not enough to cover DB requirements.");
            }

            ulong maxWantedMemory = Math.Max(minMemory, (ulong) (memoryHint * maxPercentage));
            ulong availableDynamic = minMemory >= maxWantedMemory ? 0ul : maxWantedMemory - minMemory;
            ulong availableForBuffer = (ulong) (availableDynamic * 0.05m);
            ulong bufferDynamic = Math.Min(maxBufferMem, availableForBuffer);
            ulong bufferMem = minBufferMem + bufferDynamic;
            ulong cacheDynamic = availableDynamic - bufferDynamic;
            ulong cacheMem = Math.Min(dbNeeds.PreferredMaxMemory, minCacheMem + cacheDynamic);

            Debug.Assert(bufferDynamic + cacheDynamic <= availableDynamic, "dynamic exceeded");
            Debug.Assert(bufferMem + cacheMem <= maxWantedMemory, "max wanted exceeded");
            Debug.Assert(bufferMem + cacheMem <= availableMemory, "available exceeded");

            DbGets dbGets = new DbGets(buffers, bufferMem / buffers, cacheMem);
            return dbGets;
        }

        private struct DbGets
        {
            public DbGets(uint buffers, ulong singleBufferMem, ulong cacheMem)
            {
                Buffers = buffers;
                SingleBufferMem = singleBufferMem;
                CacheMem = cacheMem;
            }

            public uint Buffers { get; set; }
            public ulong SingleBufferMem { get; set; }
            public ulong CacheMem { get; set; }
        }

        private struct DbNeeds
        {
            public DbNeeds(
                uint preferredBuffers,
                ulong preferredMinBufferMemory,
                ulong preferredMaxBufferMemory,
                ulong preferredMinMemory,
                ulong preferredMaxMemory,
                decimal preferredMemoryPercentage)
            {
                PreferredBuffers = preferredBuffers;
                PreferredMinBufferMemory = preferredMinBufferMemory;
                PreferredMaxBufferMemory = preferredMaxBufferMemory;
                PreferredMinMemory = preferredMinMemory;
                PreferredMaxMemory = preferredMaxMemory;
                PreferredMemoryPercentage = preferredMemoryPercentage;
            }

            public uint PreferredBuffers { get; set; }
            public ulong PreferredMinBufferMemory { get; set; }
            public ulong PreferredMaxBufferMemory { get; set; }
            public ulong PreferredMinMemory { get; set; }
            public ulong PreferredMaxMemory { get; set; }
            public decimal PreferredMemoryPercentage { get; set; }
        }

        private DbNeeds GetStateNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastSync ? 8u : 4u);
            // remove optimize for point lookup here?
            return new DbNeeds(
                preferredBuffers,
                1.MB(), // min buffer size
                64.MB(), // max buffer size
                4.MB(), // min block cache
                128.GB(), // max block cache
                1m); // db memory %
        }

        private DbNeeds GetBlockInfosNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastBlocks ? 4u : 2u);
            // remove optimize for point lookup here?
            return new DbNeeds(
                preferredBuffers,
                1.MB(), // min buffer size
                8.MB(), // max buffer size
                1.MB(), // min block cache
                512.MB(), // max block cache
                0.02m); // db memory %
        }

        private DbNeeds GetHeaderNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastBlocks ? 4u : 2u);
            return new DbNeeds(
                preferredBuffers,
                1.MB(), // min buffer size
                8.MB(), // max buffer size
                1.MB(), // min block cache
                1.GB(), // max block cache
                0.02m); // db memory %
        }

        private DbNeeds GetBlocksNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastBlocks ? 4u : 2u);
            return new DbNeeds(
                preferredBuffers,
                4.MB(), // min buffer size
                64.MB(), // max buffer size
                8.MB(), // min block cache
                2.GB(), // max block cache
                0.04m); // db memory %
        }

        private DbNeeds GetReceiptsNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastBlocks ? 4u : 2u);
            return new DbNeeds(
                preferredBuffers,
                2.MB(), // min buffer size
                64.MB(), // max buffer size
                8.MB(), // min block cache
                2.GB(), // max block cache
                0.01m); // db memory %
        }

        private DbNeeds GetPendingTxNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            return new DbNeeds(
                4,
                1.MB(), // min buffer size
                16.MB(), // max buffer size
                2.MB(), // min block cache
                128.MB(), // max block cache
                0.01m); // db memory %
        }

        private DbNeeds GetCodeNeeds(uint cpuCount, ISyncConfig syncConfig)
        {
            uint preferredBuffers = Math.Min(cpuCount, syncConfig.FastSync ? 4u : 2u);
            return new DbNeeds(
                preferredBuffers,
                1.MB(), // min buffer size
                4.MB(), // max buffer size
                2.MB(), // min block cache
                32.MB(), // max block cache
                0); // db memory %
        }

        private void AssignNettyMemory(INetworkConfig networkConfig, uint cpuCount)
        {
            NettyMemory = (ulong) Math.Min((long) 512.MB(), (long) (0.2 * _remainingMemory));
            ulong estimate = NettyMemoryEstimator.Estimate(cpuCount, networkConfig.NettyArenaOrder);
            ValidateCpuCount(cpuCount);

            /* first of all we assume that the mainnet will be heavier than any other chain on the side */
            /* we will leave the arena order as in config if it is set to a non-default value */
            if (networkConfig.NettyArenaOrder != INetworkConfig.DefaultNettyArenaOrder)
            {
                if (_logger.IsInfo)
                    _logger.Info($"Leaving {nameof(INetworkConfig.NettyArenaOrder)} " +
                                 $"at {networkConfig.NettyArenaOrder} as it is set to non-default.");
            }
            else
            {
                int targetNettyArenaOrder = INetworkConfig.DefaultNettyArenaOrder;
                for (int i = networkConfig.NettyArenaOrder; i > 0; i--)
                {
                    estimate = NettyMemoryEstimator.Estimate(cpuCount, i);
                    ulong maxAvailableFoNetty = NettyMemory;
                    if (estimate <= maxAvailableFoNetty)
                    {
                        targetNettyArenaOrder = i;
                        break;
                    }
                }

                networkConfig.NettyArenaOrder = Math.Min(11, targetNettyArenaOrder);
            }

            NettyMemory = estimate;
        }

        private static void ValidateCpuCount(uint cpuCount)
        {
            if (cpuCount < 1U)
            {
                throw new ArgumentOutOfRangeException(nameof(cpuCount), "CPU count has to be >= 1.");
            }
        }
    }
}