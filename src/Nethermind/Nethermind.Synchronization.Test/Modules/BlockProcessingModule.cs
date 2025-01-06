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
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
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

            // NOTE: The ordering of block preprocessor is not guarenteed
            .AddComposite<CompositeBlockPreprocessorStep, IBlockPreprocessorStep>()
            .Add<IBlockPreprocessorStep, RecoverSignatures>()

            // It need CodeInfoRepository which seems to need the pre warmer and so is recreated for each
            // read only env, so I guess its specific to each world state.
            // I dont set it in the `MainBlockProcessingContext` configurer because it need to be overridden for testing.
            .AddScoped<IChainHeadInfoProvider, ChainHeadInfoProvider>()

            .AddSingleton<MainBlockProcessingContext, ILifetimeScope>((ctx) =>
            {
                IReceiptConfig receiptConfig = ctx.Resolve<IReceiptConfig>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;

                ILifetimeScope innerScope = ctx.BeginLifetimeScope((processingCtxBuilder) =>
                {

                    processingCtxBuilder

                        // Technically, these block are not main block processor specific and can be put at higher level which make it overridable.
                        // I just put it here since block producer does not need it yet, so in case I miss something
                        // that is specific to block producer it will throw so I put it here first.
                        .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>(CreateRewardCalculator)
                        .AddScoped<IBlockProcessor, BlockProcessor>()
                        .AddScoped<IVirtualMachine, VirtualMachine>()
                        .AddScoped<ITransactionProcessor, TransactionProcessor>()
                        .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
                        .AddScoped<IBlockhashStore, BlockhashStore>()
                        .AddScoped<IBlockhashProvider, BlockhashProvider>()
                        .AddScoped<BlockchainProcessor>()
                        .AddScoped<ICodeInfoRepository, IWorldState>(CreateCodeInfoRepository)

                        // These are definitely main block processing specific
                        .AddScoped(worldState)
                        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                        .AddScoped(new BlockchainProcessor.Options
                        {
                            StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                            DumpOptions = initConfig.AutoDump
                        })

                        // Chain head info need the main chain head info which codeInfoRepository, which seems to be
                        // not global. So TxPool is actually in main block processing context.
                        // The RecoverSignature actually need TxPool, which complicate things as it mean that txpool
                        // should not be in main block processing context.
                        .AddScoped<IComparer<Transaction>, ITransactionComparerProvider>(txComparer => txComparer.GetDefaultComparer())
                        .AddScoped<ITxPool, TxPool.TxPool>()

                        // And finally, to wrap things up.
                        .AddScoped<MainBlockProcessingContext>()
                        ;

                    if (blocksConfig.PreWarmStateOnBlockProcessing)
                    {
                        processingCtxBuilder
                            .AddScoped<PreBlockCaches>((worldState as IPreBlockCaches)!.Caches)
                            .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                            .AddScoped<ReadOnlyTxProcessingEnvFactory>();
                    }
                });

                return innerScope.Resolve<MainBlockProcessingContext>();
            })
            .Map<MainBlockProcessingContext, IBlockProcessingQueue>(ctx => ctx.BlockProcessingQueue)
            .Map<MainBlockProcessingContext, ITxPool>(ctx => ctx.TxPool)

            .Add<BlockProducerEnvFactory>()
            .AddSingleton<BlockProducerContext, ILifetimeScope>((ctx) =>
            {
                // Note: This is modelled after TestBlockchain, not prod
                // But the additional tx pool source is not set.
                BlockProducerEnvFactory envFactory = ctx.Resolve<BlockProducerEnvFactory>();
                BlockProducerEnv env = envFactory.Create();

                ILifetimeScope innerScope = ctx.BeginLifetimeScope((producerCtx) =>
                {
                    producerCtx
                        .AddScoped<ITxSource>(env.TxSource)
                        .AddScoped<IBlockchainProcessor>(env.ChainProcessor)
                        .AddScoped<IWorldState>(env.ReadOnlyStateProvider)
                        .AddScoped<IBlockProducer, TestBlockProducer>()
                        .AddScoped<IBlockProducerRunner, StandardBlockProducerRunner>()
                        .AddScoped<BlockProducerContext>();
                });

                return innerScope.Resolve<BlockProducerContext>();
            })


            .Map<BlockProducerContext, IBlockProducerRunner>(ctx => ctx.BlockProducerRunner)
            .AddSingleton<IManualBlockProductionTrigger, BuildBlocksWhenRequested>()
            .Bind<IManualBlockProductionTrigger, IBlockProductionTrigger>()
            .AddSingleton<ProducedBlockSuggester>()
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

    public record MainBlockProcessingContext(ILifetimeScope lifetimeScope, BlockchainProcessor BlockchainProcessor, ITxPool TxPool): IAsyncDisposable
    {
        public IBlockProcessingQueue BlockProcessingQueue => BlockchainProcessor;

        public async ValueTask DisposeAsync()
        {
            await lifetimeScope.DisposeAsync();
        }
    }

    public record BlockProducerContext(
        ILifetimeScope LifetimeScope,
        IBlockProducerRunner BlockProducerRunner
    ): IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await LifetimeScope.DisposeAsync();
        }
    }
}
