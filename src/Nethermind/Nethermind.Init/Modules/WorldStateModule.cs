// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Init;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init.Modules;

public class WorldStateModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<FlatStateActivationPolicy>()
            .AddSingleton<FlatWorldStateManager>()

            .Bind<IWorldStateManager, FlatWorldStateManager>()

            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)

            // Apply FlatStateActivationPolicy when the state boundary is resolved during startup.
            .AddSingleton<FlatStateBoundary>()
            .AddSingleton<IStateBoundary, FlatStateActivationPolicy, FlatStateBoundary>((_, boundary) => boundary)

            .Bind<IFullStateFinder, FlatFullStateFinder>()
            .Bind<ISnapTrieFactory, FlatSnapTrieFactory>()
            .Bind<ITreeSyncStore, FlatTreeSyncStore>()
            .Bind<IBalHealing, FlatBalHealing>()

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()

            .AddSingleton<IFinalizedStateProvider, ReorgDepthFinalizedStateProvider>()

            // Register the backend-independent verify-trie admin RPC.
            .RegisterSingletonJsonRpcModule<IVerifyTrieAdminRpcModule, VerifyTrieAdminRpcModule>()
        ;

        // Register the verify-trie diagnostic step.
        if (initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
            builder.AddStep(typeof(RunVerifyTrie));
    }
}
