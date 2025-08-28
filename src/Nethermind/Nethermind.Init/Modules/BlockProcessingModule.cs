// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
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

            // Block processing components
            .AddSingleton<IPrecompileChecker, EthereumPrecompileChecker>()
            .AddScoped<ITransactionProcessor, TransactionProcessor>()
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            .AddScoped<IVirtualMachine, VirtualMachine>()
            .AddScoped<IBlockhashProvider, BlockhashProvider>()
            .AddSingleton<IReadOnlyTxProcessingEnvFactory, AutoReadOnlyTxProcessingEnvFactory>()

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
            ;
    }
}
