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
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
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

        public IBlockProducerFactory BlockProducerFactory => new AuRaBlockProducerFactory(BlockProducerStarter);

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
            yield return typeof(RegisterAuRaRpcModules);
            yield return typeof(StartBlockProcessorAuRa);
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
                ;

            if (specParam.BlockGasLimitContractTransitions?.Any() == true)
            {
                builder.AddSingleton<IHeaderValidator, AuRaHeaderValidator>();
            }
        }
    }

    public class AuRaBlockProducerFactory(StartBlockProducerAuRa blockProducerStarter) : IBlockProducerFactory
    {
        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            return blockProducerStarter.BuildProducer(additionalTxSource);
        }
    }
}
