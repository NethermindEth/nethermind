// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Services;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa")]

namespace Nethermind.Consensus.AuRa
{
    /// <summary>
    /// Consensus plugin for AuRa setup.
    /// </summary>
    public class AuRaPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        private AuRaNethermindApi? _nethermindApi;
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public string SealEngineType => Core.SealEngineType.AuRa;

        private StartBlockProducerAuRa? _blockProducerStarter;

        private StartBlockProducerAuRa BlockProducerStarter => _blockProducerStarter ??= _nethermindApi!.CreateStartBlockProducer();

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;
        public ValueTask DisposeAsync()
        {
            return default;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi as AuRaNethermindApi;
            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer()
        {
            return BlockProducerStarter!.BuildProducer();
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            return new StandardBlockProducerRunner(
                BlockProducerStarter.CreateTrigger(),
                _nethermindApi.BlockTree,
                blockProducer);
        }

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
                .AddSingleton<NethermindApi, AuRaNethermindApi>()
                .AddSingleton<AuRaChainSpecEngineParameters>(specParam)
                .AddDecorator<IBetterPeerStrategy, AuRaBetterPeerStrategy>()
                .Add<StartBlockProducerAuRa>() // Note: Stateful. Probably just some strange unintentional side effect though.
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
                .AddStep(typeof(LoadGenesisBlockAuRa))

                // Block processing components
                .AddSingleton<IBlockValidationModule, AuraValidationModule>()
                .AddSingleton<IMainProcessingModule, AuraMainProcessingModule>()
                .AddScoped<IAuRaValidator, NullAuRaValidator>() // Note: for main block processor this is not the case
                .AddScoped<IBlockProcessor, AuRaBlockProcessor>()

                .AddSingleton<IRewardCalculatorSource, AuRaRewardCalculator.AuRaRewardCalculatorSource>()
                .AddSingleton<IValidSealerStrategy, ValidSealerStrategy>()
                .AddSingleton<IAuRaStepCalculator, AuRaChainSpecEngineParameters, ITimestamper, ILogManager>((param, timestamper, logManager)
                    => new AuRaStepCalculator(param.StepDuration, timestamper, logManager))
                .AddSingleton<AuRaSealValidator>()
                .Bind<ISealValidator, AuRaSealValidator>()
                .AddSingleton<ISealer, AuRaSealer>()
                .AddSingleton<AuRaGasLimitOverrideFactory>()

                // Rpcs
                .AddSingleton<IHealthHintService, AuraHealthHintService>()

                ;

            if (specParam.BlockGasLimitContractTransitions?.Any() == true)
            {
                builder.AddSingleton<IHeaderValidator, AuRaHeaderValidator>();
            }

            if (Rlp.GetStreamDecoder<ValidatorInfo>() is null) Rlp.RegisterDecoder(typeof(ValidatorInfo), new ValidatorInfoDecoder());
        }

        /// <summary>
        /// Some validation component that is active in rpc and validation but not in block produccer.
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

                IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
                ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

                AuRaContractGasLimitOverride? gasLimitOverride = gasLimitOverrideFactory.GetGasLimitCalculator();

                builder.AddSingleton(txFilter);
                if (contractRewriter is not null) builder.AddSingleton(contractRewriter);
                if (gasLimitOverride is not null) builder.AddSingleton(gasLimitOverride);
            }
        }
    }
}
