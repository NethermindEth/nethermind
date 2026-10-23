// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Init.Steps;

public sealed class ConfigureMemoryAllocators(
    IInitConfig initConfig,
    INetworkConfig networkConfig,
    ILogManager logManager,
    MallocHelper? mallocHelper = null)
    : IStep
{
    private const uint NettyPageSize = 8192;

    private readonly ILogger _logger = logManager.GetClassLogger<ConfigureMemoryAllocators>();
    private readonly MallocHelper _mallocHelper = mallocHelper ?? MallocHelper.Instance;

    public Task Execute(CancellationToken _)
    {
#pragma warning disable CS0612
        if (initConfig.MemoryHint is not null && _logger.IsWarn)
            _logger.Warn($"Init.{nameof(IInitConfig.MemoryHint)} is deprecated and ignored. Set Db.{nameof(IDbConfig.SharedBlockCacheSize)} to change the database cache size.");
#pragma warning restore CS0612

        SetupMallocOpts();
        ConfigureNetty((uint)Environment.ProcessorCount);

        return Task.CompletedTask;
    }

    private void SetupMallocOpts()
    {
        if (initConfig.DisableMallocOpts) return;

        // The MMAP threshold is the minimum size of allocation before glibc uses mmap to allocate the memory
        // instead of sbrk. This means the whole allocation can be released on its own without incurring fragmentation
        // but its not reusable and incur a system call. It turns out by default this value is dynamically adjusted
        // from 128KB up to 32MB in size on 64bit machine, so most of the memory reduction is due to just disabling
        // this auto adjustment.
        // Setting this essentially reduces the maximum size of a `hole` in the heap, but it causes extra system call.
        // On 16C/32T machine, this reduces memory usage by about 7GB.
        // There aren't much difference between 16KB to 64KB, but the system cpu time increase slightly as threshold
        // lowers. 4k significantly increase cpu system time.
        bool success = _mallocHelper.MallOpt(MallocHelper.Option.M_MMAP_THRESHOLD, (int)64UL.KiB);
        if (!success && _logger.IsDebug) _logger.Debug("Unable to set M_MAP_THRESHOLD");
    }

    private void ConfigureNetty(uint cpuCount)
    {
        uint arenaCount = Math.Min(cpuCount * 2, networkConfig.MaxNettyArenaCount);

        // For some reason needs to be half page size to get page size
        Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", (NettyPageSize / 2).ToString((IFormatProvider?)null));

        // Set PooledByteBufferAllocator.Default configuration.
        // Set it to a fixed 1 MB, 1 arena. Code should prefer NethermindBuffers.Default instead as it is easier to override.
        ConfigureDefaultPooledByteBufferAllocator(8, 1);

        NethermindBuffers.Default = NethermindBuffers.CreateAllocator(networkConfig.NettyArenaOrder, arenaCount);
        NethermindBuffers.RlpxAllocator = NethermindBuffers.CreateAllocator(networkConfig.NettyArenaOrder, arenaCount);

        // 1 MB chunk as discovery should be fairly small and does not do much.
        NethermindBuffers.DiscoveryAllocator = NethermindBuffers.CreateAllocator(8, arenaCount);
    }

    private void ConfigureDefaultPooledByteBufferAllocator(int arenaOrder, uint arenaCount)
    {
        // Need to set these early, or otherwise if the allocator is used ahead of these setting, these config
        // will not take affect

        Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", arenaOrder.ToString());

        // Arena count is capped because if its too high, the memory budget per arena can get too low causing
        // a very small chunk size. Any allocation of size higher than a chunk will essentially be unpooled triggering LOH.
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
}
