// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
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
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.OverridableEnv;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class BlockProcessingModule : Module
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
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            .AddScoped<IVirtualMachine, VirtualMachine>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()
            .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
            .AddScoped<IBlockhashStore, BlockhashStore>()
            .AddScoped<IBlockProcessor, BlockProcessor>()
            .AddScoped<IWithdrawalProcessor, WithdrawalProcessor>()
            .AddScoped<IExecutionRequestsProcessor, ExecutionRequestsProcessor>()
            .AddScoped<IBlockchainProcessor, BlockchainProcessor>()
            .AddScoped<IRewardCalculator, IRewardCalculatorSource, ITransactionProcessor>((rewardSource, txP) => rewardSource.Get(txP))
            .AddScoped<BlockProcessor.IBlockProductionTransactionPicker, ISpecProvider, IBlocksConfig>((specProvider, blocksConfig) =>
                new BlockProcessor.BlockProductionTransactionPicker(specProvider, blocksConfig.BlockProductionMaxTxKilobytes))
            .AddSingleton<IReadOnlyTxProcessingEnvFactory, AutoReadOnlyTxProcessingEnvFactory>()

            .AddSingleton<IOverridableEnvFactory, OverridableEnvFactory>()
            .AddScopedOpenGeneric(typeof(IOverridableEnv<>), typeof(DisposableScopeOverridableEnv<>))

            // Transaction executor used by main block validation and rpc.
            .AddScoped<IValidationTransactionExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()

            // Block production components
            .AddSingleton<IRewardCalculatorSource>(NoBlockRewards.Instance)
            .AddSingleton<ISealValidator>(NullSealEngine.Instance)
            .AddSingleton<ISealer>(NullSealEngine.Instance)
            .AddSingleton<ISealEngine, SealEngine>()

            .AddSingleton<ISimulateTransactionProcessorFactory>(SimulateTransactionProcessorFactory.Instance)
            .AddSingleton<IBlockProducerEnvFactory, BlockProducerEnvFactory>()
            .AddSingleton<IBlockProducerTxSourceFactory, TxPoolTxSourceFactory>()

            .AddSingleton<IGasPriceOracle, IBlockFinder, ISpecProvider, ILogManager, IBlocksConfig>((blockTree, specProvider, logManager, blocksConfig) =>
                new GasPriceOracle(
                    blockTree,
                    specProvider,
                    logManager,
                    blocksConfig.MinGasPrice
                ))
            ;
    }
}
