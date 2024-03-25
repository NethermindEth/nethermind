// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using DotNetty.Buffers;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.TxPool;

namespace Nethermind.Init
{
    /// <summary>
    /// Applies changes to the NetworkConfig and the DbConfig so to adhere to the max memory limit hint.
    /// </summary>
    public class MemoryHintMan
    {
        private readonly ILogger _logger;
        private readonly MallocHelper _mallocHelper;

        public MemoryHintMan(ILogManager logManager, MallocHelper? mallocHelper = null)
        {
            _mallocHelper = mallocHelper ?? MallocHelper.Instance;
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
            TotalMemory = initConfig.MemoryHint ?? 2.GB();
            ValidateCpuCount(cpuCount);

            checked
            {
                SetupMallocOpts(initConfig);

                if (_logger.IsInfo) _logger.Info("Setting up memory allowances");
                if (_logger.IsInfo) _logger.Info($"  Memory hint:        {TotalMemory / 1000 / 1000,5} MB");
                _remainingMemory = initConfig.MemoryHint ?? 2.GB();
                _remainingMemory -= GeneralMemory;
                if (_logger.IsInfo) _logger.Info($"  General memory:     {GeneralMemory / 1000 / 1000,5} MB");
                AssignPeersMemory(networkConfig);
                _remainingMemory -= PeersMemory;
                if (_logger.IsInfo) _logger.Info($"  Peers memory:       {PeersMemory / 1000 / 1000,5} MB");
                AssignNettyMemory(networkConfig, cpuCount);
                _remainingMemory -= NettyMemory;
                if (_logger.IsInfo) _logger.Info($"  Netty memory:       {NettyMemory / 1000 / 1000,5} MB");
                AssignTxPoolMemory(txPoolConfig);
                _remainingMemory -= TxPoolMemory;
                if (_logger.IsInfo) _logger.Info($"  Mempool memory:     {TxPoolMemory / 1000 / 1000,5} MB");
                AssignFastBlocksMemory(syncConfig);
                _remainingMemory -= FastBlocksMemory;
                if (_logger.IsInfo) _logger.Info($"  Fast blocks memory: {FastBlocksMemory / 1000 / 1000,5} MB");
                AssignTrieCacheMemory(dbConfig);
                _remainingMemory -= TrieCacheMemory;
                if (_logger.IsInfo) _logger.Info($"  Trie memory:        {TrieCacheMemory / 1000 / 1000,5} MB");
                UpdateDbConfig(dbConfig, initConfig);
                _remainingMemory -= DbMemory;
                if (_logger.IsInfo) _logger.Info($"  DB memory:          {DbMemory / 1000 / 1000,5} MB");

            }
        }

        private void SetupMallocOpts(IInitConfig initConfig)
        {
            if (initConfig.DisableMallocOpts) return;

            if (_logger.IsDebug) _logger.Debug("Setting malloc parameters..");

            // The MMAP threshold is the minimum size of allocation before glibc uses mmap to allocate the memory
            // instead of sbrk. This means the whole allocation can be released on its own without incurring fragmentation
            // but its not reusable and incur a system call. It turns out by default this value is dynamically adjusted
            // from 128KB up to 32MB in size on 64bit machine, so most of the memory reduction is due to just disabling
            // this auto adjustment.
            // Setting this essentially reduces the maximum size of a `hole` in the heap, but it causes extra system call.
            // On 16C/32T machine, this reduces memory usage by about 7GB.
            // There aren't much difference between 16KB to 64KB, but the system cpu time increase slightly as threshold
            // lowers. 4k significantly increase cpu system time.
            bool success = MallocHelper.Instance.MallOpt(MallocHelper.Option.M_MMAP_THRESHOLD, (int)64.KiB());
            if (!success && _logger.IsDebug) _logger.Debug("Unable to set M_MAP_THRESHOLD");
        }

        private long _remainingMemory;

        public long TotalMemory = 1024 * 1024 * 1024;
        public long GeneralMemory { get; } = 32.MB();
        public long FastBlocksMemory { get; private set; }
        public long DbMemory { get; private set; }
        public long NettyMemory { get; private set; }
        public long TxPoolMemory { get; private set; }
        public long PeersMemory { get; private set; }
        public long TrieCacheMemory { get; private set; }

        private void AssignTrieCacheMemory(IDbConfig dbConfig)
        {
            TrieCacheMemory = (long)(0.2 * _remainingMemory);
            dbConfig.StateDbRowCacheSize = (ulong)TrieCacheMemory;
        }

        private void AssignPeersMemory(INetworkConfig networkConfig)
        {
            PeersMemory = networkConfig.MaxActivePeers.MB();
            if (PeersMemory > _remainingMemory * 0.75)
            {
                throw new InvalidDataException(
                    $"Memory hint is not enough to satisfy the {nameof(NetworkConfig)}.{nameof(INetworkConfig.MaxActivePeers)}. " +
                    $"Assign at least MaxActivePeers * ~1MB * ~1.25 of memory.");
            }
        }

        private void AssignTxPoolMemory(ITxPoolConfig txPoolConfig)
        {
            long hashCacheMemory = txPoolConfig.Size / 4L * 1024L * 128L;
            if ((_remainingMemory * 0.05) < hashCacheMemory)
            {
                hashCacheMemory = Math.Min((long)(_remainingMemory * 0.05), hashCacheMemory);
            }

            MemoryAllowance.TxHashCacheSize = (int)(hashCacheMemory / 128);
            hashCacheMemory = MemoryAllowance.TxHashCacheSize * 128;

            long txPoolMemory = txPoolConfig.Size * 40.KB() + hashCacheMemory;
            if (txPoolMemory > _remainingMemory * 0.5)
            {
                throw new InvalidDataException(
                    $"Memory hint is not enough to satisfy the {nameof(TxPoolConfig)}.{nameof(TxPoolConfig.Size)}");
            }

            TxPoolMemory = txPoolMemory;
        }

        private void AssignFastBlocksMemory(ISyncConfig syncConfig)
        {
            if (syncConfig.FastSync)
            {
                if (!syncConfig.DownloadBodiesInFastSync && !syncConfig.DownloadReceiptsInFastSync)
                {
                    FastBlocksMemory = Math.Min(128.MB(), (long)(0.1 * _remainingMemory));
                }
                else
                {
                    FastBlocksMemory = Math.Min(1.GB(), (long)(0.1 * _remainingMemory));
                }

                Synchronization.MemoryAllowance.FastBlocksMemory = (ulong)FastBlocksMemory;
            }
        }

        private void UpdateDbConfig(IDbConfig dbConfig, IInitConfig initConfig)
        {
            if (initConfig.DiagnosticMode == DiagnosticMode.MemDb)
            {
                DbMemory = _remainingMemory;
                return;
            }

            if (dbConfig.SkipMemoryHintSetting) return;

            DbMemory = _remainingMemory;
            dbConfig.SharedBlockCacheSize = (ulong)DbMemory;
        }

        private struct DbNeeds
        {
            public DbNeeds(
                uint preferredBuffers,
                long preferredMinBufferMemory,
                long preferredMaxBufferMemory,
                long preferredMinMemory,
                long preferredMaxMemory,
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
            public long PreferredMinBufferMemory { get; set; }
            public long PreferredMaxBufferMemory { get; set; }
            public long PreferredMinMemory { get; set; }
            public long PreferredMaxMemory { get; set; }
            public decimal PreferredMemoryPercentage { get; set; }
        }

        private void AssignNettyMemory(INetworkConfig networkConfig, uint cpuCount)
        {
            ValidateCpuCount(cpuCount);

            NettyMemory = Math.Min(512.MB(), (long)(0.2 * _remainingMemory));

            uint arenaCount = (uint)Math.Min(cpuCount * 2, networkConfig.MaxNettyArenaCount);

            long estimate = NettyMemoryEstimator.Estimate(arenaCount, networkConfig.NettyArenaOrder);

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
                int targetNettyArenaOrder = INetworkConfig.MaxNettyArenaOrder;
                for (int i = INetworkConfig.MaxNettyArenaOrder; i > 0; i--)
                {
                    estimate = NettyMemoryEstimator.Estimate(arenaCount, i);
                    long maxAvailableFoNetty = NettyMemory;
                    if (estimate <= maxAvailableFoNetty)
                    {
                        targetNettyArenaOrder = i;
                        break;
                    }
                }

                networkConfig.NettyArenaOrder = Math.Min(11, targetNettyArenaOrder);
            }

            NettyMemory = estimate;

            // Need to set these early, or otherwise if the allocator is used ahead of these setting, these config
            // will not take affect

            Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", networkConfig.NettyArenaOrder.ToString());

            // Arena count is capped because if its too high, the memory budget per arena can get too low causing
            // a very small chunk size. Any allocation of size higher than a chunk will essentially be unpooled triggering LOH.
            // For example, on 16C32T machine, the default arena count is 64. Goerli with its default 128MB budget will
            // cause the chunk size to be 2 MB. Mainnet with its 383MB budget will cause the chunk size to be 4 MB (lower
            // power of two from 5.9 MB).
            //
            // When a thread first try to allocate from the pooled byte buffer, a threadlocal is created and pick
            // one of the many arena, binding the thread to it. So arena count is like sharding.
            //
            // An arena consist of a list of chunks. Usually only one remain most of the time per arena.
            // Multiple allocation will share a chunk as long as there is enough space. If no chunk with enough space
            // is available, a new chunk is created, triggering a LOH allocation. There are also a thread level cache,
            // so a chunk usually is not immediately freed once buffer allocated to it is released.
            //
            // Heap arena frees a chunk by just dereferencing, leaving GC to take it later.
            // Direct arena holds a pinned `GCHandle` per chunk and calls `GCHandle.Free` to release the chunk.
            // We never use any direct arena, but it does not take up memory because of that.
            Environment.SetEnvironmentVariable("io.netty.allocator.numHeapArenas", arenaCount.ToString());
            Environment.SetEnvironmentVariable("io.netty.allocator.numDirectArenas", arenaCount.ToString());

            if (PooledByteBufferAllocator.Default.Metric.HeapArenas().Count != arenaCount)
            {
                _logger.Warn("unable to set netty pooled byte buffer config");
            }
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
