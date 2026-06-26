// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
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

        // Clamp to valid ranges so a misconfigured value degrades gracefully instead of throwing from
        // the cache/SeqlockCache constructors and aborting node startup with an opaque error.
        int depth = Math.Max(0, blocksConfig.HeadStateCacheDepth);
        int accountBits = Math.Clamp(blocksConfig.HeadStateCacheAccountSetsBits, 1, SeqlockCache<AddressAsKey, Account>.MaxSetsBits);
        int storageBits = Math.Clamp(blocksConfig.HeadStateCacheStorageSetsBits, 1, SeqlockCache<AddressAsKey, Account>.MaxSetsBits);

        builder
            .AddSingleton(new HeadStateCache(depth, accountBits, storageBits))

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
