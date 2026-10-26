// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using DotNetty.Buffers;
using Nethermind.Api;
using Nethermind.Core.Memory;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class ConfigureMemoryAllocatorsTests
{
    [Test]
    public void Sets_malloc_options_unless_disabled([Values] bool disableMallocOpts)
    {
        MallocHelper mallocHelper = Substitute.For<MallocHelper>();
        InitConfig initConfig = new() { DisableMallocOpts = disableMallocOpts };

        new ConfigureMemoryAllocators(initConfig, new NetworkConfig(), LimboLogs.Instance, mallocHelper).Execute(CancellationToken.None);

        mallocHelper.Received(disableMallocOpts ? 0 : 1).MallOpt(MallocHelper.Option.M_MMAP_THRESHOLD, Arg.Any<int>());
    }

    [TestCase(-1, INetworkConfig.DefaultNettyArenaOrder)]
    [TestCase(9, 9)]
    public void Uses_configured_netty_arena_order_or_default_when_negative_and_caps_arena_count(int configuredOrder, int expectedOrder)
    {
        NetworkConfig networkConfig = new() { NettyArenaOrder = configuredOrder, MaxNettyArenaCount = 1 };

        new ConfigureMemoryAllocators(new InitConfig(), networkConfig, LimboLogs.Instance, Substitute.For<MallocHelper>()).Execute(CancellationToken.None);

        int expectedChunkSize = PooledByteBufferAllocator.DefaultPageSize << expectedOrder;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((PooledByteBufferAllocator)NethermindBuffers.Default).Metric.ChunkSize, Is.EqualTo(expectedChunkSize));
            Assert.That(((PooledByteBufferAllocator)NethermindBuffers.RlpxAllocator).Metric.ChunkSize, Is.EqualTo(expectedChunkSize));
            Assert.That(((PooledByteBufferAllocator)NethermindBuffers.Default).Metric.HeapArenas().Count, Is.EqualTo(1));
        }
    }
}
