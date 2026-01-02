// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

public interface IWitnessGeneratingBlockProcessingEnvScope : IDisposable
{
    IWitnessGeneratingBlockProcessingEnv Env { get; }
}

public interface IWitnessGeneratingBlockProcessingEnvFactory
{
    IWitnessGeneratingBlockProcessingEnvScope CreateScope();
}

public class WitnessGeneratingBlockProcessingEnvFactory(
    ILifetimeScope rootLifetimeScope,
    IReadOnlyTrieStore readOnlyTrieStore,
    IDbProvider dbProvider,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    private sealed class Scope(ILifetimeScope envLifetimeScope) : IWitnessGeneratingBlockProcessingEnvScope
    {
        public IWitnessGeneratingBlockProcessingEnv Env { get; } = envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();

        public void Dispose() => envLifetimeScope.Dispose();
    }

    public IWitnessGeneratingBlockProcessingEnvScope CreateScope()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        WitnessCapturingTrieStore trieStore = new(readOnlyDbProvider.StateDb, readOnlyTrieStore);
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState worldState = new WorldState(new TrieStoreScopeProvider(trieStore, readOnlyDbProvider.CodeDb, logManager), logManager);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(worldState)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv>(builder =>
                new WitnessGeneratingBlockProcessingEnv(
                    builder.Resolve<ISpecProvider>(),
                    builder.Resolve<IWorldState>() as WorldState,
                    trieStore,
                    builder.Resolve<IReadOnlyBlockTree>(),
                    builder.Resolve<ISealValidator>(),
                    builder.Resolve<IRewardCalculator>(),
                    builder.Resolve<IHeaderStore>(),
                    logManager)));

        return new Scope(envLifetimeScope);
    }
}
