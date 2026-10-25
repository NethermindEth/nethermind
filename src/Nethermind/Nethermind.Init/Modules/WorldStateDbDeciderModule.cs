// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
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

            .AddSingleton<IWorldStateManager, FlatStateActivationPolicy, ISyncConfig, IPruningConfig, ILogManager, Func<FlatWorldStateManager>, Func<PruningTrieStoreModule.PruningTrieStateFactoryOutput>>(
                (policy, syncConfig, pruningConfig, logManager, flatFactory, patriciaFactory) =>
                {
                    if (!policy.ShouldTurnOnFlatDb())
                    {
                        return patriciaFactory().WorldStateManager;
                    }

                    // FullPrunerFactory is never constructed on Flat (WorldStateDbDecider skips the patricia graph).
                    // Warn only when the operator set an automatic full-prune trigger; Hybrid is the default Mode.
                    if (pruningConfig.FullPruningTrigger != FullPruningTrigger.Manual)
                    {
                        ILogger logger = logManager.GetClassLogger<WorldStateDbDeciderModule>();
                        if (logger.IsWarn)
                            logger.Warn("Copy-style full pruning is patricia-only and is ignored on the Flat backend. admin_prune returns disabled. Use FlatDb.HistoryRetention / history pruning instead of Pruning.FullPruningTrigger.");
                    }

                    // Flat state can always serve snap requests; set before InitializeNetwork registers capabilities.
                    syncConfig.SnapServingEnabled ??= true;
                    return flatFactory();
                })

            // Injected into the block tree at construction time, so it must not resolve
            // IWorldStateManager (whose graph resolves the block tree back).
            .AddSingleton<IStateBoundary, FlatStateActivationPolicy, IFlatDbConfig, Func<FlatStateBoundary>, Func<StateBoundaryStore>>(
                (policy, flatDbConfig, flatBoundary, trieBoundary) =>
                    !policy.ShouldTurnOnFlatDb() ? trieBoundary()
                    // ImportFlatDb copies trie state at blockTree.Head, so during import the head
                    // must still rewind to the trie's persisted block; the flat DB is empty then.
                    : flatDbConfig.ImportFromPruningTrieState
                        ? new ImportFallbackStateBoundary(flatBoundary(), trieBoundary())
                        : flatBoundary())

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
                        : (IFullStateFinder)patriciaFactory())

            .AddSingleton<IBalHealing, FlatStateActivationPolicy, Func<FlatBalHealing>>(
                (policy, flatFactory) =>
                    policy.ShouldTurnOnFlatDb()
                        ? flatFactory()
                        : NoopBalHealing.Instance);

    private sealed class ImportFallbackStateBoundary(
        FlatStateBoundary flat,
        StateBoundaryStore trie) : IStateBoundary
    {
        public ulong? OldestStateBlock => flat.OldestStateBlock;
        public ulong? RetentionWindowBlocks => flat.RetentionWindowBlocks;
        public ulong? BestPersistedState => flat.BestPersistedState ?? trie.BestPersistedState;
    }
}
