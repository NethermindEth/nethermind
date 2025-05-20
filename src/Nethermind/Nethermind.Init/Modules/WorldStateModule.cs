// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State;

namespace Nethermind.Init.Modules;

public class WorldStateModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // Most config actually done in factory. We just call `Build` and then get back components from its output.
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            .Map<IWorldStateManager, PruningTrieStateFactoryOutput>((o) => o.WorldStateManager)
            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)

            // Used by sync code
            .Map<INodeStorage, PruningTrieStateFactoryOutput>((m) => m.NodeStorage)

            // Some admin rpc to trigger verify trie and pruning
            .Map<IPruningTrieStateAdminRpcModule, PruningTrieStateFactoryOutput>((m) => m.AdminRpcModule)
            .RegisterSingletonJsonRpcModule<IPruningTrieStateAdminRpcModule>()

            .AddSingleton<IReadOnlyStateProvider, ChainHeadReadOnlyStateProvider>()

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()
            ;
    }

    // Just a wrapper to easily extract the output of `PruningTrieStateFactory` which do the actual initializations.
    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }
        public INodeStorage NodeStorage { get; }
        public IPruningTrieStateAdminRpcModule AdminRpcModule { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, INodeStorage mainNodeStorage, IPruningTrieStateAdminRpcModule adminRpc) = factory.Build();
            WorldStateManager = worldStateManager;
            NodeStorage = mainNodeStorage;
            AdminRpcModule = adminRpc;
        }
    }
}
