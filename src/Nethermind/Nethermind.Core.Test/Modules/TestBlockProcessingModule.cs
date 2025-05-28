// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class TestBlockProcessingModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IRewardCalculatorSource>(NoBlockRewards.Instance)
            .AddSingleton<ISealValidator>(NullSealEngine.Instance)
            .AddSingleton<ITransactionComparerProvider, TransactionComparerProvider>()
            // NOTE: The ordering of block preprocessor is not guarenteed
            .AddComposite<IBlockPreprocessorStep, CompositeBlockPreprocessorStep>()
            .AddSingleton<CompositeBlockPreprocessorStep>()
            .AddSingleton<IBlockPreprocessorStep, RecoverSignatures>()

            // Yea, for some reason, the ICodeInfoRepository need to be the main one for ChainHeadInfoProvider to work.
            // Like, is ICodeInfoRepository suppose to be global? Why not just IStateReader.
            .AddKeyedSingleton<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState), (ctx) =>
            {
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;
                PreBlockCaches? preBlockCaches = (worldState as IPreBlockCaches)?.Caches;
                return new CodeInfoRepository(preBlockCaches?.PrecompileCache);
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

            .AddSingleton<ITxPool, TxPool.TxPool>()
            .AddSingleton<INonceManager, IChainHeadInfoProvider>((chainHeadInfoProvider) => new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider))

            // These are common between processing and production and worldstate-ful, so they should be scoped instead
            // of singleton.
            .AddScoped<IBlockchainProcessor, BlockchainProcessor>()
            .AddScoped<IBlockProcessor, BlockProcessor>()
            .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>((rewardCalculatorSource, txProcessor) => rewardCalculatorSource.Get(txProcessor))
            .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
            .AddScoped<IBlockhashStore, BlockhashStore>()
            .AddScoped<IExecutionRequestsProcessor, ExecutionRequestsProcessor>()
            .AddScoped<IWithdrawalProcessor, WithdrawalProcessor>()
            .AddScoped<BlockchainProcessor>()

            // The main block processing pipeline, anything that requires the use of the main IWorldState is wrapped
            // in a `MainBlockProcessingContext`.
            .AddSingleton<MainBlockProcessingContext, ILifetimeScope>(ConfigureMainBlockProcessingContext)
            // Then component that has no ambiguity is extracted back out.
            .Map<IBlockProcessingQueue, MainBlockProcessingContext>(ctx => ctx.BlockProcessingQueue)
            .Bind<IMainProcessingContext, MainBlockProcessingContext>()


            // Seems to be only used by block producer.
            .AddScoped<IGasLimitCalculator, TargetAdjustedGasLimitCalculator>()
            .AddScoped<ITxSource, TxPoolTxSource>()
            .AddScoped<ITxFilterPipeline, ILogManager, ISpecProvider, IBlocksConfig>(TxFilterPipelineBuilder.CreateStandardFilteringPipeline)
            .AddScoped<ISealEngine, SealEngine>()
            .AddScoped<IComparer<Transaction>, ITransactionComparerProvider>(txComparer => txComparer.GetDefaultComparer())
            .AddScoped<BlockProducerEnvFactory>()

            // Much like block validation, anything that require the use of IWorldState in block producer, is wrapped in
            // a `BlockProducerContext`.
            .AddSingleton<BlockProducerContext, ILifetimeScope>(ConfigureBlockProducerContext)
            // And then we extract it back out.
            .Map<IBlockProducerRunner, BlockProducerContext>(ctx => ctx.BlockProducerRunner)
            .Bind<IBlockProductionTrigger, IManualBlockProductionTrigger>()

            // Something else entirely. Just some wrapper over things.
            .AddSingleton<IManualBlockProductionTrigger, BuildBlocksWhenRequested>()
            .AddSingleton<ProducedBlockSuggester>()
            .ResolveOnServiceActivation<ProducedBlockSuggester, IBlockProducerRunner>()

            .AddSingleton<ISigner>(NullSigner.Instance)
            .AddSingleton<IGasPriceOracle, IBlockFinder, ISpecProvider, ILogManager, IBlocksConfig>((blockTree, specProvider, logManager, blocksConfig) =>
                new GasPriceOracle(
                    blockTree,
                    specProvider,
                    logManager,
                    blocksConfig.MinGasPrice
                ))

            ;
    }

    private MainBlockProcessingContext ConfigureMainBlockProcessingContext(ILifetimeScope ctx)
    {
        IReceiptConfig receiptConfig = ctx.Resolve<IReceiptConfig>();
        IInitConfig initConfig = ctx.Resolve<IInitConfig>();
        IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
        IWorldState mainWorldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;
        ICodeInfoRepository mainCodeInfoRepository =
            ctx.ResolveNamed<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState));

        ILifetimeScope innerScope = ctx.BeginLifetimeScope((processingCtxBuilder) =>
        {
            processingCtxBuilder
                // These are main block processing specific
                .AddScoped<ICodeInfoRepository>(mainCodeInfoRepository)
                .AddScoped(mainWorldState)
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor,
                    BlockProcessor.BlockValidationTransactionsExecutor>()
                .AddScoped(new BlockchainProcessor.Options
                {
                    StoreReceiptsByDefault = receiptConfig.StoreReceipts,
                    DumpOptions = initConfig.AutoDump
                })
                .AddScoped<GenesisLoader>()

                // And finally, to wrap things up.
                .AddScoped<MainBlockProcessingContext>()
                .Bind<IBlockchainProcessor, BlockchainProcessor>()
                .Bind<IBlockProcessingQueue, BlockchainProcessor>()
                ;

            if (blocksConfig.PreWarmStateOnBlockProcessing)
            {
                processingCtxBuilder
                    .AddScoped<PreBlockCaches>((mainWorldState as IPreBlockCaches)!.Caches)
                    .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                    ;
            }
        });

        return innerScope.Resolve<MainBlockProcessingContext>();
    }

    private BlockProducerContext ConfigureBlockProducerContext(ILifetimeScope ctx)
    {
        // Note: This is modelled after TestBlockchain, not prod
        IWorldState thisBlockProducerWorldState = ctx.Resolve<IWorldStateManager>().CreateResettableWorldState();
        ILifetimeScope innerScope = ctx.BeginLifetimeScope((producerCtx) =>
        {
            producerCtx
                .AddScoped<IWorldState>(thisBlockProducerWorldState)

                // Block producer specific
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped(BlockchainProcessor.Options.NoReceipts)
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
                .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()

                .AddScoped<ICodeInfoRepository, CodeInfoRepository>()

                // TODO: What is this suppose to be?
                .AddScoped<IBlockProducer, TestBlockProducer>()

                .AddScoped<IBlockProducerRunner, StandardBlockProducerRunner>()
                .AddScoped<BlockProducerContext>();
        });

        return innerScope.Resolve<BlockProducerContext>();
    }
}
