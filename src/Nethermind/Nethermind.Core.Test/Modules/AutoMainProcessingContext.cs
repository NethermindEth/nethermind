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
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Core.Test.Modules;

public record AutoMainProcessingContext : IMainProcessingContext, IAsyncDisposable
{
    public AutoMainProcessingContext(
        ILifetimeScope rootLifetimeScope,
        IReceiptConfig receiptConfig,
        IBlocksConfig blocksConfig,
        IInitConfig initConfig,
        IBlockValidationModule[] blockValidationModules,
        IWorldStateManager worldStateManager,
        [KeyFilter(nameof(IWorldStateManager.GlobalWorldState))] ICodeInfoRepository mainCodeInfoRepository)
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
                .AddScoped(new BlockchainProcessor.Options
                {
                    StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump
                })
                .AddScoped<GenesisLoader>()

                // And finally, to wrap things up.
                .AddScoped<MainProcessingContext>()
                ;

            if (blocksConfig.PreWarmStateOnBlockProcessing)
            {
                builder
                    .AddScoped<PreBlockCaches>((mainWorldState as IPreBlockCaches)!.Caches)
                    .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                    ;
            }
        });

        _mainProcessingContext = innerScope.Resolve<MainProcessingContext>();

        LifetimeScope = innerScope;
        GenesisLoader = innerScope.Resolve<GenesisLoader>();
    }

    public async ValueTask DisposeAsync()
    {
        await LifetimeScope.DisposeAsync();
    }

    private MainProcessingContext _mainProcessingContext;
    public ILifetimeScope LifetimeScope { get; init; }
    public IBlockchainProcessor BlockchainProcessor => _mainProcessingContext.BlockchainProcessor;
    public IWorldState WorldState => _mainProcessingContext.WorldState;
    public IBlockProcessor BlockProcessor => _mainProcessingContext.BlockProcessor;
    public ITransactionProcessor TransactionProcessor => _mainProcessingContext.TransactionProcessor;
    public GenesisLoader GenesisLoader { get; init; }
}
