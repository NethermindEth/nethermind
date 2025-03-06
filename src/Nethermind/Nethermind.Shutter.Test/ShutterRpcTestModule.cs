// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Synchronization;
using NSubstitute;

namespace Nethermind.Shutter.Test;

public class ShutterRpcTestModule(ShutterTestBlockchain chain): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new MergeRpcModule())
            .AddSource(new FallbackToFieldFromApi<ShutterTestBlockchain>(
                directlyDeclaredOnly: false,
                allowRedundantRegistration: true))
            .AddSingleton(chain)
            .AddSingleton<EngineModuleTestHelpers>()

            // Some things to make compatible with TestBlockchain. Not needed if full merge plugin is loaded
            // but then the `ShutterTestBlockchain` will not be used.
            .AddSingleton<BeaconSync>()
            .Bind<IBeaconSyncStrategy, BeaconSync>()
            .Bind<IMergeSyncController, BeaconSync>()
            .AddSingleton<IBeaconPivot, BeaconPivot>()
            .AddSingleton<ISyncConfig>(new SyncConfig())
            .AddSingleton<IBlocksConfig>(new BlocksConfig())
            .AddSingleton<IBlockCacheService, BlockCacheService>()
            .AddSingleton<IInvalidChainTracker, InvalidChainTracker>()
            .AddSingleton<GCKeeper>(new GCKeeper(NoGCStrategy.Instance, LimboLogs.Instance))
            .AddSingleton<IPeerRefresher>(Substitute.For<IPeerRefresher>());
    }
}
