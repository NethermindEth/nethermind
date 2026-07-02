// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Init.Modules;

internal class WorldStateDbDeciderModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddSingleton<FlatStateActivationPolicy>()

            .AddSingleton<IWorldStateManager, FlatStateActivationPolicy, ISyncConfig, Func<FlatWorldStateManager>, Func<PruningTrieStoreModule.PruningTrieStateFactoryOutput>>(
                (policy, syncConfig, flatFactory, patriciaFactory) =>
                {
                    if (!policy.ShouldTurnOnFlatDb()) return patriciaFactory().WorldStateManager;
                    if (syncConfig.PartialArchiveEnabled)
                    {
                        throw new InvalidConfigurationException(
                            $"{nameof(ISyncConfig.PartialArchiveEnabled)} is not supported with the flat state layout; use the HalfPath trie layout.", -1);
                    }
                    // Flat state can always serve snap requests; set before InitializeNetwork registers capabilities.
                    syncConfig.SnapServingEnabled ??= true;
                    return flatFactory();
                })

            .AddSingleton<IPruningTrieStateAdminRpcModule, FlatStateActivationPolicy, Func<FlatWorldStateModule.PruningTrieStateAdminRpcModuleStub>, Func<PruningTrieStoreModule.PruningTrieStateFactoryOutput>>(
                (policy, flatFactory, patriciaFactory) =>
                    policy.ShouldTurnOnFlatDb()
                        ? flatFactory()
                        : patriciaFactory().AdminRpcModule)

            .AddSingleton<ISnapTrieFactory, FlatStateActivationPolicy, Func<FlatSnapTrieFactory>, Func<PatriciaSnapTrieFactory>>(
                (policy, flatFactory, patriciaFactory) =>
                    policy.ShouldTurnOnFlatDb()
                        ? flatFactory()
                        : (ISnapTrieFactory)patriciaFactory())

            .AddSingleton<ITreeSyncStore, FlatStateActivationPolicy, Func<FlatTreeSyncStore>, Func<PatriciaTreeSyncStore>>(
                (policy, flatFactory, patriciaFactory) =>
                    policy.ShouldTurnOnFlatDb()
                        ? flatFactory()
                        : (ITreeSyncStore)patriciaFactory())

            .AddSingleton<IFullStateFinder, FlatStateActivationPolicy, Func<FlatFullStateFinder>, Func<FullStateFinder>>(
                (policy, flatFactory, patriciaFactory) =>
                    policy.ShouldTurnOnFlatDb()
                        ? flatFactory()
                        : (IFullStateFinder)patriciaFactory());
}
