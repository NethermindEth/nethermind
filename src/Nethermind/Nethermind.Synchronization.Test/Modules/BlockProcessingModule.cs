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
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
            .AddSingleton<IBlockPreprocessorStep, RecoverSignatures>()

            // Yea, for some reason, the ICodeInfoRepository need to be the main one for ChainHeadInfoProvider to work.
            // Like, is ICodeInfoRepository suppose to be global? Why not just IStateReader.
            .AddKeyedSingleton<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState), (ctx) =>
            {
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;
                PreBlockCaches? preBlockCaches = (worldState as IPreBlockCaches)?.Caches;
                return new CodeInfoRepository (preBlockCaches?.PrecompileCache);
            })
            .AddSingleton<IChainHeadInfoProvider, IComponentContext>((ctx) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                IStateReader stateReader = ctx.Resolve<IStateReader>();
                // need this to be the right one.
                ICodeInfoRepository codeInfoRepository = ctx.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));
                return new ChainHeadInfoProvider(specProvider, blockTree, stateReader, codeInfoRepository);
            })
            .AddSingleton<IComparer<Transaction>, ITransactionComparerProvider>(txComparer => txComparer.GetDefaultComparer())
            .AddSingleton<ITxPool, TxPool.TxPool>()

            // These are common between processing and production and worldstate-ful, so they should be scoped instead
            // of singleton.
            .AddScoped<IBlockchainProcessor, BlockchainProcessor>()
            .AddScoped<IBlockProcessor, BlockProcessor>()
            .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>(CreateRewardCalculator)
            .AddScoped<ITransactionProcessor, TransactionProcessor>()
            .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
            .AddScoped<IBlockhashStore, BlockhashStore>()
            .AddScoped<IVirtualMachine, VirtualMachine>()
            .AddScoped<BlockchainProcessor>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()

            .AddSingleton<MainBlockProcessingContext, ILifetimeScope>((ctx) =>
            {
                IReceiptConfig receiptConfig = ctx.Resolve<IReceiptConfig>();
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;
                ICodeInfoRepository mainCodeInfoRepository = ctx.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));

                ILifetimeScope innerScope = ctx.BeginLifetimeScope((processingCtxBuilder) =>
                {

                    processingCtxBuilder

                        // These are main block processing specific
                        .AddScoped<ICodeInfoRepository>(mainCodeInfoRepository)
                        .AddScoped(worldState)
                        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                        .AddScoped(new BlockchainProcessor.Options
                        {
                            StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                            DumpOptions = initConfig.AutoDump
                        })
                        .AddScoped<GenesisLoader>()

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

            .Add<BlockProducerEnvFactory>()
            .AddSingleton<BlockProducerContext, ILifetimeScope>((ctx) =>
            {
                // Note: This is modelled after TestBlockchain, not prod
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().CreateResettableWorldState();
                ILifetimeScope innerScope = ctx.BeginLifetimeScope((producerCtx) =>
                {
                    producerCtx
                        .AddScoped<ITxSource, TxPoolTxSource>()
                        .AddScoped<ITxFilterPipeline, ILogManager, ISpecProvider, IBlocksConfig>(TxFilterPipelineBuilder.CreateStandardFilteringPipeline)
                        .AddDecorator<OneTimeChainProcessor, IBlockchainProcessor>()
                        .AddScoped(BlockchainProcessor.Options.NoReceipts)
                        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
                        .AddScoped<IWithdrawalProcessor, WithdrawalProcessor>()
                        .AddDecorator<BlockProductionWithdrawalProcessor, IWithdrawalProcessor>()
                        .AddScoped<ICodeInfoRepository, CodeInfoRepository>()

                        .AddScoped<IWorldState>(worldState)
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

    private ITxValidator CreateTxValidator(ISpecProvider specProvider)
    {
        return new TxValidator(specProvider.ChainId);
    }

    public record MainBlockProcessingContext(
        ILifetimeScope LifetimeScope,
        BlockchainProcessor BlockchainProcessor,
        GenesisLoader GenesisLoader): IAsyncDisposable
    {
        public IBlockProcessingQueue BlockProcessingQueue => BlockchainProcessor;

        public async ValueTask DisposeAsync()
        {
            await LifetimeScope.DisposeAsync();
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
