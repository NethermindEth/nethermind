// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Init.Modules;

public class WorldStateModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder

            .AddSingleton<INodeStorageFactory>(ctx =>
            {
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ISyncConfig syncConfig = ctx.Resolve<ISyncConfig>();
                IDb stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);
                ILogManager logManager = ctx.Resolve<ILogManager>();
                INodeStorageFactory nodeStorageFactory = new NodeStorageFactory(initConfig.StateDbKeyScheme, logManager);
                nodeStorageFactory.DetectCurrentKeySchemeFrom(stateDb);

                syncConfig.SnapServingEnabled |= syncConfig.SnapServingEnabled is null
                                                 && nodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.HalfPath or null
                                                 && initConfig.StateDbKeyScheme != INodeStorage.KeyScheme.Hash;

                if (nodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.Hash
                    || initConfig.StateDbKeyScheme == INodeStorage.KeyScheme.Hash)
                {
                    // Special case in case its using hashdb, use a slightly different database configuration.
                    if (stateDb is ITunableDb tunableDb) tunableDb.Tune(ITunableDb.TuneType.HashDb);
                }

                return nodeStorageFactory;
            })

            // Used by sync code and trie store
            .AddSingleton<INodeStorage>(ctx =>
            {
                IDb stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);
                INodeStorageFactory nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                return nodeStorageFactory.WrapKeyValueStore(stateDb);
            })

            // Most config actually done in factory. We just call `Build` and then get back components from its output.
            .AddSingleton<MainPruningTrieStoreFactory>() // This part is done separately so that triestore can be obtained in test.
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            .Map<IWorldStateManager, PruningTrieStateFactoryOutput>((o) => o.WorldStateManager)
            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader)

            // Some admin rpc to trigger verify trie and pruning
            .Map<IPruningTrieStateAdminRpcModule, PruningTrieStateFactoryOutput>((m) => m.AdminRpcModule)
            .RegisterSingletonJsonRpcModule<IPruningTrieStateAdminRpcModule>()

            .AddSingleton<IReadOnlyStateProvider, ChainHeadReadOnlyStateProvider>()

            // Prevent multiple concurrent verify trie.
            .AddSingleton<IVerifyTrieStarter, VerifyTrieStarter>()
            ;

        if (initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            builder.AddStep(typeof(RunVerifyTrie));
        }
    }

    // Just a wrapper to easily extract the output of `PruningTrieStateFactory` which do the actual initializations.
    private class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }
        public IPruningTrieStateAdminRpcModule AdminRpcModule { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, IPruningTrieStateAdminRpcModule adminRpc) = factory.Build();
            WorldStateManager = worldStateManager;
            AdminRpcModule = adminRpc;
        }
    }
}
