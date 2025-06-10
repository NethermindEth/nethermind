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
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
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

        public IEnumerable<StepInfo> GetSteps()
        {
            yield return typeof(InitializeBlockchainAuRa);
            yield return typeof(LoadGenesisBlockAuRa);
        }

        public IModule Module => new AuraModule(chainSpec);

        public Type ApiType => typeof(AuRaNethermindApi);
    }

    public class AuraModule(ChainSpec chainSpec) : Module
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

                .AddSingleton<IRewardCalculatorSource, AuRaRewardCalculator.AuRaRewardCalculatorSource>()
                .AddSingleton<IValidSealerStrategy, ValidSealerStrategy>()
                .AddSingleton<IAuRaStepCalculator, AuRaChainSpecEngineParameters, ITimestamper, ILogManager>((param, timestamper, logManager)
                    => new AuRaStepCalculator(param.StepDuration, timestamper, logManager))
                .AddSingleton<AuRaSealValidator>()
                .Bind<ISealValidator, AuRaSealValidator>()
                .AddSingleton<ISealer, AuRaSealer>()

                .AddSingleton<IHealthHintService, AuraHealthHintService>()

                .AddSingleton<IRpcModuleFactory<ITraceRpcModule>, AuRaTraceModuleFactory>()
                .AddSingleton<IAuRaBlockProcessorFactory, AuRaBlockProcessorFactory>()
                .AddSingleton<IRpcModuleFactory<IDebugRpcModule>, AuRaDebugModuleFactory>()

                ;

            if (specParam.BlockGasLimitContractTransitions?.Any() == true)
            {
                builder.AddSingleton<IHeaderValidator, AuRaHeaderValidator>();
            }

            if (Rlp.GetStreamDecoder<ValidatorInfo>() is null) Rlp.RegisterDecoder(typeof(ValidatorInfo), new ValidatorInfoDecoder());
        }
    }
}
