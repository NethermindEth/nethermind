// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
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

public class MainProcessingContext : IMainProcessingContext, IAsyncDisposable
{
    public MainProcessingContext(
        ILifetimeScope rootLifetimeScope,
        IReceiptConfig receiptConfig,
        IBlocksConfig blocksConfig,
        IInitConfig initConfig,
        IBlockValidationModule[] blockValidationModules,
        IMainProcessingModule[] mainProcessingModules,
        IWorldStateManager worldStateManager,
        [KeyFilter(nameof(IWorldStateManager.GlobalWorldState))] ICodeInfoRepository mainCodeInfoRepository,
        CompositeBlockPreprocessorStep compositeBlockPreprocessorStep,
        IBlockTree blockTree,
        ILogManager logManager)
    {

        var mainWorldState = worldStateManager.GlobalWorldState;
        ILifetimeScope innerScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
        {
            builder
                // These are main block processing specific
                .AddScoped<ICodeInfoRepository>(mainCodeInfoRepository)
                .AddSingleton<IWorldState>(mainWorldState)
                .AddModule(blockValidationModules)
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
                .AddScoped<GenesisLoader>()
                .AddModule(mainProcessingModules)

                .AddScoped<IBlockchainProcessor, IBranchProcessor>((branchProcessor) => new BlockchainProcessor (
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
    public GenesisLoader GenesisLoader => _components.GenesisLoader;

    private record Components(
        ITransactionProcessor TransactionProcessor,
        IBranchProcessor BranchProcessor,
        IBlockProcessor BlockProcessor,
        IBlockchainProcessor BlockchainProcessor,
        IWorldState WorldState,
        GenesisLoader GenesisLoader
    );
}
