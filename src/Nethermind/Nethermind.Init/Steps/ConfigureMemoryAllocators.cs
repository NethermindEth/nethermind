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
        SetupMallocOpts();
        ConfigureNetty((uint)Environment.ProcessorCount);

        return Task.CompletedTask;
    }

    private void SetupMallocOpts()
    {
        if (initConfig.DisableMallocOpts) return;

        // A fixed threshold stops glibc from raising it dynamically up to 32 MB, which fragments the heap
        // (about 7 GB less memory on 16C/32T). Lower values cost more system CPU time.
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

        // Each thread binds to one arena, and every new chunk is a LOH allocation. Direct arenas are never used,
        // so they take no memory.
        Environment.SetEnvironmentVariable("io.netty.allocator.numHeapArenas", arenaCount.ToString());
        Environment.SetEnvironmentVariable("io.netty.allocator.numDirectArenas", arenaCount.ToString());
        if (PooledByteBufferAllocator.Default.Metric.HeapArenas().Count != arenaCount)
        {
            _logger.Warn("unable to set netty pooled byte buffer config");
        }
    }
}
