// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init.Modules;

public class MainProcessingContext : IMainProcessingContext, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler, IAsyncDisposable
{
    public MainProcessingContext(
        ILifetimeScope rootLifetimeScope,
        IReceiptConfig receiptConfig,
        IBlocksConfig blocksConfig,
        IInitConfig initConfig,
        IBlockValidationModule[] blockValidationModules,
        IMainProcessingModule[] mainProcessingModules,
        IWorldStateManager worldStateManager,
        CompositeBlockPreprocessorStep compositeBlockPreprocessorStep,
        IBlockTree blockTree,
        IPrecompileProvider precompileProvider,
        ILogManager logManager)
    {

        var mainWorldState = worldStateManager.GlobalWorldState;
        ILifetimeScope innerScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                // These are main block processing specific
                .AddSingleton<IWorldState>(mainWorldState)
                .AddModule(blockValidationModules)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddSingleton<BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler>(this)
                .AddModule(mainProcessingModules)

                .AddScoped<IBlockchainProcessor, IBranchProcessor>((branchProcessor) => new BlockchainProcessor(
                    blockTree!,
                    branchProcessor,
                    compositeBlockPreprocessorStep,
                    worldStateManager.GlobalStateReader,
                    logManager,
                    new BlockchainProcessor.Options
                    {
                        StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                        DumpOptions = initConfig.AutoDump
                    })
                {
                    IsMainProcessor = true // Manual construction because of this flag
                })

                // And finally, to wrap things up.
                .AddScoped<Components>()
                ;

            if (blocksConfig.PreWarmStateOnBlockProcessing)
            {
                builder
                    .AddScoped<PreBlockCaches>((mainWorldState as IPreBlockCaches)!.Caches)
                    .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                    .AddDecorator<ICodeInfoRepository>((ctx, originalCodeInfoRepository) =>
                    {
                        PreBlockCaches preBlockCaches = ctx.Resolve<PreBlockCaches>();
                        // Note: The use of FrozenDictionary means that this cannot be used for other processing env also due to risk of memory leak.
                        return new CachedCodeInfoRepository(precompileProvider, originalCodeInfoRepository, preBlockCaches?.PrecompileCache);
                    })
                    ;
            }
        });

        _components = innerScope.Resolve<Components>();

        LifetimeScope = innerScope;
    }

    public async ValueTask DisposeAsync()
    {
        await LifetimeScope.DisposeAsync();
    }

    private Components _components;
    public ILifetimeScope LifetimeScope { get; init; }
    public IBlockchainProcessor BlockchainProcessor => _components.BlockchainProcessor;
    public IWorldState WorldState => _components.WorldState;
    public IBranchProcessor BranchProcessor => _components.BranchProcessor;
    public IBlockProcessor BlockProcessor => _components.BlockProcessor;
    public ITransactionProcessor TransactionProcessor => _components.TransactionProcessor;
    public IGenesisLoader GenesisLoader => _components.GenesisLoader;
    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
    public void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs)
    {
        TransactionProcessed?.Invoke(this, txProcessedEventArgs);
    }

    private record Components(
        ITransactionProcessor TransactionProcessor,
        IBranchProcessor BranchProcessor,
        IBlockProcessor BlockProcessor,
        IBlockchainProcessor BlockchainProcessor,
        IWorldState WorldState,
        IGenesisLoader GenesisLoader
    );
}
