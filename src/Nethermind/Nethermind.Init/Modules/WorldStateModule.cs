// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init.Modules;

public class WorldStateModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder
            // Stub: a backend module must override this. Load PruningTrieStoreModule or FlatWorldStateModule.
            .AddSingleton<IWorldStateManager>(_ => throw new InvalidOperationException(
                $"No world state backend registered. Load {nameof(PruningTrieStoreModule)} or {nameof(FlatWorldStateModule)}."))

            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()

            .AddSingleton<IFinalizedStateProvider, ReorgDepthFinalizedStateProvider>()

            // Admin RPC surface is common to all backends; each backend registers its implementation.
            .RegisterSingletonJsonRpcModule<IPruningTrieStateAdminRpcModule>()
        ;
}
