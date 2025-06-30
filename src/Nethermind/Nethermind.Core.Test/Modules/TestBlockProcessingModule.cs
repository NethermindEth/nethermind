// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class TestBlockProcessingModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
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

            .AddSingleton<ITxPool, TxPool.TxPool>()
            .AddSingleton<CompositeTxGossipPolicy>()
            .AddSingleton<INonceManager, IChainHeadInfoProvider>((chainHeadInfoProvider) => new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider))

            // The main block processing pipeline, anything that requires the use of the main IWorldState is wrapped
            // in a `MainBlockProcessingContext`.
            .AddSingleton<MainBlockProcessingContext, ILifetimeScope>(ConfigureMainBlockProcessingContext)
            // Then component that has no ambiguity is extracted back out.
            .Map<IBlockProcessingQueue, MainBlockProcessingContext>(ctx => ctx.BlockProcessingQueue)
            .Bind<IMainProcessingContext, MainBlockProcessingContext>()


            // Seems to be only used by block producer.
            .AddScoped<IGasLimitCalculator, TargetAdjustedGasLimitCalculator>()
            .AddScoped<IComparer<Transaction>, ITransactionComparerProvider>(txComparer => txComparer.GetDefaultComparer())

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
                .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>()
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>()
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
        IBlockProducerEnv env = ctx.Resolve<IBlockProducerEnvFactory>().Create();
        ILifetimeScope innerScope = ctx.BeginLifetimeScope((producerCtx) =>
        {
            producerCtx
                // Block producer specific things is in `IBlockProducerEnvFactory`.
                // Yea, it can be added as `AddScoped` too and then mapped out, but its clearer this way.
                .AddScoped<IWorldState>(env.ReadOnlyStateProvider)
                .AddScoped<IBlockchainProcessor>(env.ChainProcessor)
                .AddScoped<ITxSource>(env.TxSource)

                // TODO: What is this suppose to be?
                .AddScoped<IBlockProducer, TestBlockProducer>()

                .AddScoped<IBlockProducerRunner, StandardBlockProducerRunner>()
                .AddScoped<BlockProducerContext>();
        });

        return innerScope.Resolve<BlockProducerContext>();
    }
}
