// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init.Modules;

public class WorldStateModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // Stub: overridden by WorldStateDbDeciderModule which selects patricia or flat at runtime.
            .AddSingleton<IWorldStateManager>(_ => throw new InvalidOperationException(
                $"No world state backend registered. Load {nameof(WorldStateDbDeciderModule)} together with {nameof(PruningTrieStoreModule)} and {nameof(FlatWorldStateModule)}."))

            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)
            .Map<IStateBoundary, IWorldStateManager>((m) => m)

            .AddSingleton<PersistedStateWatcher>()
            .ResolveOnServiceActivation<PersistedStateWatcher, IWorldStateManager>()

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()

            .AddSingleton<IFinalizedStateProvider, ReorgDepthFinalizedStateProvider>()

            // Admin RPC surface is common to all backends; each backend registers its implementation.
            .RegisterSingletonJsonRpcModule<IPruningTrieStateAdminRpcModule>()

            // Verify-trie admin RPC is backend-agnostic; a single implementation serves both backends.
            .RegisterSingletonJsonRpcModule<IVerifyTrieAdminRpcModule, VerifyTrieAdminRpcModule>()
        ;

        // Backend-agnostic diagnostic step; VerifyTrie resolves to whichever backend is active.
        if (initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
            builder.AddStep(typeof(RunVerifyTrie));
    }
}
