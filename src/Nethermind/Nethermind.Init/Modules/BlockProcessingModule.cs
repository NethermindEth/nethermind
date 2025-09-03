// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class BlockProcessingModule(IInitConfig initConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            // Validators
            .AddSingleton<TxValidator, ISpecProvider>((spec) => new TxValidator(spec.ChainId))
            .Bind<ITxValidator, TxValidator>()
            .AddSingleton<IBlockValidator, BlockValidator>()
            .AddSingleton<IHeaderValidator, HeaderValidator>()
            .AddSingleton<IUnclesValidator, UnclesValidator>()

            // Block processing components common between rpc, validation and production
            .AddScoped<ITransactionProcessor, TransactionProcessor>()
            .AddScoped<ICodeInfoRepository, EthereumCodeInfoRepository>()
            .AddScoped<IVirtualMachine, VirtualMachine>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()
            .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
            .AddScoped<IBlockhashStore, BlockhashStore>()
            .AddScoped<IBranchProcessor, BranchProcessor>()
            .AddScoped<IBlockProcessor, BlockProcessor>()
            .AddScoped<IWithdrawalProcessor, WithdrawalProcessor>()
            .AddScoped<IExecutionRequestsProcessor, ExecutionRequestsProcessor>()
            .AddScoped<IBlockchainProcessor, BlockchainProcessor>()
            .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>((rewardSource, txP) => rewardSource.Get(txP))
            .AddScoped<BlockProcessor.IBlockProductionTransactionPicker, ISpecProvider, IBlocksConfig>((specProvider, blocksConfig) =>
                new BlockProcessor.BlockProductionTransactionPicker(specProvider, blocksConfig.BlockProductionMaxTxKilobytes))
            .AddSingleton<IReadOnlyTxProcessingEnvFactory, AutoReadOnlyTxProcessingEnvFactory>()
            .AddSingleton<IShareableTxProcessorSource, ShareableTxProcessingSource>()
            .Add<BlockchainProcessorFacade>()

            .AddSingleton<IOverridableEnvFactory, OverridableEnvFactory>()
            .AddScopedOpenGeneric(typeof(IOverridableEnv<>), typeof(DisposableScopeOverridableEnv<>))

            // Yea, for some reason, the ICodeInfoRepository need to be the main one for ChainHeadInfoProvider to work.
            // Like, is ICodeInfoRepository suppose to be global? Why not just IStateReader.
            .AddKeyedSingleton<ICodeInfoRepository>(nameof(IWorldStateManager.GlobalWorldState), (ctx) =>
            {
                IWorldState worldState = ctx.Resolve<IWorldStateManager>().GlobalWorldState;
                PreBlockCaches? preBlockCaches = (worldState as IPreBlockCaches)?.Caches;
                return new EthereumCodeInfoRepository(preBlockCaches?.PrecompileCache);
            })

            // The main block processing pipeline, anything that requires the use of the main IWorldState is wrapped
            // in a `IMainProcessingContext`.
            .AddSingleton<IMainProcessingContext, MainProcessingContext>()
            // Then component that has no ambiguity is extracted back out.
            .Map<IBlockProcessingQueue, MainProcessingContext>(ctx => (IBlockProcessingQueue)ctx.BlockchainProcessor)
            .Bind<IMainProcessingContext, MainProcessingContext>()

            // Some configuration that applies to validation and rpc but not to block producer. Plugins can add
            // modules in case they have special case where it only apply to validation and rpc but not block producer.
            .AddSingleton<IBlockValidationModule, StandardBlockValidationModule>()

            // Block production components
            .AddSingleton<IRewardCalculatorSource>(NoBlockRewards.Instance)
            .AddSingleton<ISealValidator>(NullSealEngine.Instance)
            .AddSingleton<ISealer>(NullSealEngine.Instance)
            .AddSingleton<ISealEngine, SealEngine>()

            .AddSingleton<IBlockProducerEnvFactory, BlockProducerEnvFactory>()
            .AddSingleton<IBlockProducerTxSourceFactory, TxPoolTxSourceFactory>()

            .AddSingleton<IGasPriceOracle, IBlockFinder, ISpecProvider, ILogManager, IBlocksConfig>((blockTree, specProvider, logManager, blocksConfig) =>
                new GasPriceOracle(
                    blockTree,
                    specProvider,
                    logManager,
                    blocksConfig.MinGasPrice
                ))

            // Genesis
            .AddSingleton<GenesisLoader.Config>((ctx) => new GenesisLoader.Config(
                string.IsNullOrWhiteSpace(initConfig?.GenesisHash) ? null : new Hash256(initConfig.GenesisHash),
                TimeSpan.FromMilliseconds(ctx.Resolve<IBlocksConfig>().GenesisTimeoutMs)))
            .AddScoped<IGenesisPostProcessor, NullGenesisPostProcessor>()
            .AddScoped<IGenesisBuilder, GenesisBuilder>()
            .AddScoped<GenesisLoader>()
            ;

        if (initConfig.ExitOnInvalidBlock) builder.AddStep(typeof(ExitOnInvalidBlock));
    }

    private class StandardBlockValidationModule : Module, IBlockValidationModule
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
            .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
    }
}
