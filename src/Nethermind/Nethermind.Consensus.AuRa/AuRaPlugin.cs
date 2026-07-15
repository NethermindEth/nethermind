// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Services;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.BlockLevelAccessList;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa")]

namespace Nethermind.Consensus.AuRa
{
    /// <summary>
    /// Consensus plugin for AuRa setup.
    /// </summary>
    public class AuRaPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public string SealEngineType => Core.SealEngineType.AuRa;

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;


        public IModule Module => new AuRaModule(chainSpec);

        public Type ApiType => typeof(AuRaNethermindApi);
    }

    public class AuRaModule(ChainSpec chainSpec) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);
            AuRaChainSpecEngineParameters specParam = chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>();

            builder
                .AddModule(new AuRaHeaderModule())
                .Intercept<ChainSpec>(AuRaChainSpecLoader.ProcessChainSpec)
                .AddSingleton<NethermindApi, AuRaNethermindApi>()
                .AddSingleton<AuRaChainSpecEngineParameters>(specParam)
                .AddDecorator<IBetterPeerStrategy, AuRaBetterPeerStrategy>()
                .AddSingleton<AuRaTxPoolTxSourceFactory>()
                .AddSingleton<AuRaBlockProducerEnvFactory>()
                .AddSingleton<AuRaBlockProducerFactory>()
                .Bind<IBlockProducerFactory, AuRaBlockProducerFactory>()
                .Bind<IBlockProducerRunnerFactory, AuRaBlockProducerFactory>()
                .AddSingleton<AuraStatefulComponents>()
                .AddSingleton<TxAuRaFilterBuilders>()
                .AddSingleton<PermissionBasedTxFilter.Cache>()
                .AddSingleton<IValidatorStore, ValidatorStore>()
                .AddSingleton<AuRaContractGasLimitOverride.Cache, AuRaContractGasLimitOverride.Cache>()
                .AddSingleton<ReportingContractBasedValidator.Cache>()
                .AddSingleton<IReportingValidator, IMainProcessingContext>((mainProcessingContext) =>
                    ((AuRaBlockProcessor)mainProcessingContext.BlockProcessor).AuRaValidator.GetReportingValidator())
                .AddSource(new FallbackToFieldFromApi<AuRaNethermindApi>())

                // Steps override
                .AddStep(typeof(InitializeBlockchainAuRa))

                // Block processing components
                .AddSingleton<IBlockValidationModule, AuraValidationModule>()
                .AddSingleton<IMainProcessingModule, AuraMainProcessingModule>()
                .AddScoped<IAuRaValidator, NullAuRaValidator>() // Note: for main block processor this is not the case
                .AddScoped<IBlockProcessor, AuRaBlockProcessor>()
                .AddSingleton<IAuRaBlockFinalizationManager, IBlockTree, IChainLevelInfoRepository, IValidatorStore, ILogManager, AuRaChainSpecEngineParameters>(
                    (blockTree, chainLevelInfoRepository, validatorStore, logManager, param) =>
                        new AuRaBlockFinalizationManager(blockTree, chainLevelInfoRepository, validatorStore, logManager, param.TwoThirdsMajorityTransition))

                .AddScoped<ITransactionProcessor, AuRaEthereumTransactionProcessor>()
                // Keep the AuRa processor in the BAL env: the mainnet factory hardwires
                // TransactionProcessor<EthereumGasPolicy> (dropping AuRa system-tx handling), so override it.
                .AddScoped<IBalProcessingEnvFactory, AuraBalProcessingEnvFactory>()

                .AddSingleton<IRewardCalculatorSource, AuRaRewardCalculator.AuRaRewardCalculatorSource>()
                .AddSingleton<IValidSealerStrategy, ValidSealerStrategy>()
                .AddSingleton<IAuRaStepCalculator, AuRaChainSpecEngineParameters, ITimestamper, ILogManager>((param, timestamper, logManager)
                    => new AuRaStepCalculator(param.StepDuration, timestamper, logManager))
                .AddSingleton<AuRaSealValidator>()
                .Bind<ISealValidator, AuRaSealValidator>()
                .AddSingleton<ISealer, AuRaSealer>()
                .AddSingleton<AuRaGasLimitOverrideFactory>()
                .AddScoped<IGenesisPostProcessor, AuraGenesisPostProcessor>()

                // Rpcs
                .AddSingleton<IHealthHintService, AuraHealthHintService>()

                ;

            if (specParam.BlockGasLimitContractTransitions?.Any() == true)
            {
                builder.AddSingleton<IHeaderValidator, AuRaHeaderValidator>();
            }

            if (Rlp.GetDecoder<ValidatorInfo>() is null) Rlp.RegisterDecoder(typeof(ValidatorInfo), new ValidatorInfoDecoder());
        }

        /// <summary>
        /// Some validation component that is active in RPC and validation but not in block producer.
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="specProvider"></param>
        /// <param name="txAuRaFilterBuilders"></param>
        /// <param name="gasLimitOverrideFactory"></param>
        private class AuraValidationModule(
            AuRaChainSpecEngineParameters parameters,
            TxAuRaFilterBuilders txAuRaFilterBuilders,
            AuRaGasLimitOverrideFactory gasLimitOverrideFactory
        ) : Module, IBlockValidationModule
        {
            protected override void Load(ContainerBuilder builder)
            {
                ITxFilter txFilter = txAuRaFilterBuilders.CreateAuRaTxFilter(new ServiceTxFilter());

                IDictionary<ulong, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
                (ulong, Address, byte[])[] rewriteBytecodeTimestamp = [.. parameters.RewriteBytecodeTimestampParsed];
                ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 || rewriteBytecodeTimestamp?.Length > 0 ? new(rewriteBytecode, rewriteBytecodeTimestamp) : null;

                AuRaContractGasLimitOverride? gasLimitOverride = gasLimitOverrideFactory.GetGasLimitCalculator();

                builder.AddSingleton(txFilter);
                if (contractRewriter is not null) builder.AddSingleton(contractRewriter);
                if (gasLimitOverride is not null) builder.AddSingleton(gasLimitOverride);
            }
        }
    }
}
