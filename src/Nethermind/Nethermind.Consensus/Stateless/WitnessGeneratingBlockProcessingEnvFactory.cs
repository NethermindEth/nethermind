// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Evm;
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

public sealed class ExecutionRecordingScope(ILifetimeScope envLifetimeScope) : IWitnessGeneratingBlockProcessingEnvScope
{
    public IWitnessGeneratingBlockProcessingEnv Env { get; } = envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();

    public void Dispose() => envLifetimeScope.Dispose();
}

public class WitnessGeneratingBlockProcessingEnvFactory(
    ILifetimeScope rootLifetimeScope,
    IReadOnlyTrieStore readOnlyTrieStore,
    IDbProvider dbProvider,
    IBlockValidationModule[] validationModules,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    public IWitnessGeneratingBlockProcessingEnvScope CreateScope()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        WitnessCapturingTrieStore trieStore = new(readOnlyTrieStore);
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState baseWorldState = new WorldState(
            new TrieStoreScopeProvider(trieStore, readOnlyDbProvider.CodeDb, logManager), logManager);

        IHeaderStore headerStore = rootLifetimeScope.Resolve<IHeaderStore>();
        WitnessGeneratingHeaderFinder headerFinder = new(headerStore);
        WitnessGeneratingWorldState witnessWorldState = new(baseWorldState, stateReader, trieStore, headerFinder);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(witnessWorldState)
            .AddScoped<WitnessGeneratingWorldState>(witnessWorldState)
            .AddScoped<IHeaderFinder>(headerFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            .AddModule(validationModules)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv, WitnessGeneratingBlockProcessingEnv>());

        return new ExecutionRecordingScope(envLifetimeScope);
    }
}
