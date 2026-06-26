// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Init.Modules;

/// <summary>
/// Registers the opt-in <see cref="HeadStateCache"/> that accelerates read-only RPC calls
/// (eth_call, debug_trace…) on top of the head. Enabled only when <see cref="IBlocksConfig.EnableHeadStateCache"/>
/// is set; otherwise this module registers nothing.
/// </summary>
public class HeadStateCacheModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!blocksConfig.EnableHeadStateCache) return;

        builder
            .AddSingleton(new HeadStateCache(
                blocksConfig.HeadStateCacheDepth,
                blocksConfig.HeadStateCacheAccountSetsBits,
                blocksConfig.HeadStateCacheStorageSetsBits))

            // Per-block changed-storage capture from processing, so the cache works on any node
            // independent of EIP-7928 / Block Access Lists.
            .AddSingleton<HeadStateDeltaBuffer>()

            // RPC-only: the shareable source (BlockchainBridge and friends) reads through the head cache,
            // while block processing keeps using the plain IReadOnlyTxProcessingEnvFactory.
            .AddSingleton<HeadCachedReadOnlyTxProcessingEnvFactory>()
            .AddSingleton<IShareableTxProcessorSource>(ctx =>
                new ShareableTxProcessingSource(ctx.Resolve<HeadCachedReadOnlyTxProcessingEnvFactory>()))

            // Keeps the cache coherent with the canonical head; activated alongside the shareable source.
            .AddSingleton<HeadStateCacheUpdater>()
            .ResolveOnServiceActivation<HeadStateCacheUpdater, IShareableTxProcessorSource>();
    }
}
