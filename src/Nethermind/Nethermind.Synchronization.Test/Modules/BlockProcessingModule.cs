// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.Test.Modules;

public class BlockProcessingModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IBlockValidator, BlockValidator>()
            .AddSingleton<ITxValidator, ISpecProvider>(CreateTxValidator)
            .AddSingleton<IHeaderValidator, HeaderValidator>()
            .AddSingleton<IUnclesValidator, UnclesValidator>()
            .AddSingleton<IRewardCalculatorSource>(NoBlockRewards.Instance)
            .AddSingleton<ISealValidator>(NullSealEngine.Instance)
            .AddSingleton<ITransactionComparerProvider, TransactionComparerProvider>()

            .AddSingleton<MainBlockProcessingContext, ILifetimeScope>((ctx) =>
            {
                IReceiptConfig receiptConfig = ctx.Resolve<IReceiptConfig>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;

                ILifetimeScope innerScope = ctx.BeginLifetimeScope((processingCtxBuilder) =>
                {
                    // TODO: The ordering is not guarenteed
                    processingCtxBuilder
                        .RegisterComposite<CompositeBlockPreprocessorStep, IBlockPreprocessorStep>();

                    processingCtxBuilder

                        // TODO: These might be exactly the same for block building or other block processor,
                        //   if so, then it should be at upper level.
                        .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>(CreateRewardCalculator)
                        .AddScoped<IBlockPreprocessorStep, RecoverSignatures>()
                        .AddScoped<IBlockProcessor, BlockProcessor>()
                        .AddScoped<IVirtualMachine, VirtualMachine>()
                        .AddScoped<ITransactionProcessor, TransactionProcessor>()
                        .AddScoped<ICodeInfoRepository, IWorldState>(CreateCodeInfoRepository)
                        .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
                        .AddScoped<IBlockhashStore, BlockhashStore>()
                        .AddScoped<IBlockhashProvider, BlockhashProvider>()
                        .AddScoped<BlockchainProcessor>()

                        // TODO: Prewarmer

                        // These are definitely main block processing specific
                        .AddScoped(worldState)
                        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                        .AddScoped(new BlockchainProcessor.Options
                        {
                            StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                            DumpOptions = initConfig.AutoDump
                        })

                        // Chain head info need the main codeInfoRepository, which may use prewarmer.
                        // So TxPool is actually in main block processing context.
                        .AddScoped<IChainHeadInfoProvider, ChainHeadInfoProvider>()
                        .AddScoped<IComparer<Transaction>, ITransactionComparerProvider>(txComparer => txComparer.GetDefaultComparer())
                        .AddScoped<ITxPool, TxPool.TxPool>()

                        // And finally, to wrap things up.
                        .AddScoped<MainBlockProcessingContext>()
                        ;
                });

                return innerScope.Resolve<MainBlockProcessingContext>();
            })

            .Map<MainBlockProcessingContext, IBlockProcessingQueue>(ctx => ctx.BlockProcessingQueue)
            ;


    }

    private IRewardCalculator CreateRewardCalculator(IRewardCalculatorSource rewardCalculatorSource, ITransactionProcessor transactionProcessor)
    {
        return rewardCalculatorSource.Get(transactionProcessor);
    }

    private ICodeInfoRepository CreateCodeInfoRepository(IWorldState worldState)
    {
        PreBlockCaches? preBlockCaches = (worldState as IPreBlockCaches)?.Caches;
        return new CodeInfoRepository (preBlockCaches?.PrecompileCache);
    }

    private ITxValidator CreateTxValidator(ISpecProvider specProvider)
    {
        return new TxValidator(specProvider.ChainId);
    }

    public record MainBlockProcessingContext(
        ILifetimeScope LifetimeScope,
        BlockchainProcessor BlockchainProcessor): IAsyncDisposable
    {
        public IBlockProcessingQueue BlockProcessingQueue => BlockchainProcessor;

        public async ValueTask DisposeAsync()
        {
            await LifetimeScope.DisposeAsync();
        }
    }
}
