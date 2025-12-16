// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnvFactory
{
    IWitnessGeneratingBlockProcessingEnv Create();
}

public class WitnessGeneratingBlockProcessingEnvFactory(
    ILifetimeScope rootLifetimeScope,
    IReadOnlyTrieStore readOnlyTrieStore,
    IDbProvider dbProvider,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    public IWitnessGeneratingBlockProcessingEnv Create()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        WitnessCapturingTrieStore trieStore = new(readOnlyDbProvider.StateDb, readOnlyTrieStore);
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState worldState = new WorldState(trieStore, readOnlyDbProvider.CodeDb, logManager, null, true);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(worldState)
            .AddScoped<WitnessCapturingTrieStore>(trieStore)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv>(builder =>
                new WitnessGeneratingBlockProcessingEnv(
                    builder.Resolve<ISpecProvider>(),
                    builder.Resolve<IWorldState>() as WorldState,
                    builder.Resolve<WitnessCapturingTrieStore>(),
                    builder.Resolve<IReadOnlyBlockTree>(),
                    builder.Resolve<ISealValidator>(),
                    builder.Resolve<IRewardCalculator>(),
                    builder.Resolve<IHeaderStore>(),
                    logManager)));

        rootLifetimeScope.Disposer.AddInstanceForAsyncDisposal(envLifetimeScope);
        return envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();
    }
}
