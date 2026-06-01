// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

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
    IWorldStateManager worldStateManager,
    IDbProvider dbProvider,
    IBlockValidationModule[] validationModules,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    public IWitnessGeneratingBlockProcessingEnvScope CreateScope()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        WitnessCapturingTrieStore trieStore = new(worldStateManager.CreateReadOnlyTrieStore());
        WitnessCapturingCodeDb codeDb = new(readOnlyDbProvider.CodeDb);
        IStateReader stateReader = new StateReader(trieStore, codeDb, logManager);
        IWorldState baseWorldState = new WorldState(
            new TrieStoreScopeProvider(trieStore, codeDb, logManager), logManager, witnessMode: true);

        IHeaderStore headerStore = rootLifetimeScope.Resolve<IHeaderStore>();
        WitnessGeneratingHeaderFinder headerFinder = new(headerStore);
        WitnessGeneratingWorldState witnessWorldState = new(baseWorldState, worldStateManager.GlobalStateReader, trieStore, codeDb, headerFinder);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(witnessWorldState)
            .AddScoped<WitnessGeneratingWorldState>(witnessWorldState)
            .AddScoped<IHeaderFinder>(headerFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            .AddModule(validationModules)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv, WitnessGeneratingBlockProcessingEnv>()
            .AddScoped<IBlockAccessListManager>(ctx => new BlockAccessListManager(
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<ISpecProvider>(),
                ctx.Resolve<IBlockhashProvider>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IWithdrawalProcessorFactory>(),
                ctx.ResolveOptional<PrewarmerEnvFactory>(),
                ctx.ResolveOptional<PreBlockCaches>(),
                ctx.ResolveOptional<IReadOnlyTxProcessingEnvFactory>(),
                witnessMode: true
            )));

        return new ExecutionRecordingScope(envLifetimeScope);
    }
}
