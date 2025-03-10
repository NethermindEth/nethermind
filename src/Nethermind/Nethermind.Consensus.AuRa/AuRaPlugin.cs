// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa")]

namespace Nethermind.Consensus.AuRa
{
    /// <summary>
    /// Consensus plugin for AuRa setup.
    /// </summary>
    public class AuRaPlugin(ChainSpec chainSpec) : IConsensusPlugin, ISynchronizationPlugin
    {
        private AuRaNethermindApi? _nethermindApi;
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public string SealEngineType => Core.SealEngineType.AuRa;

        private StartBlockProducerAuRa? _blockProducerStarter;

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;
        public ValueTask DisposeAsync()
        {
            return default;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi as AuRaNethermindApi;
            if (_nethermindApi is not null)
            {
                _blockProducerStarter = new(_nethermindApi);
            }
            return Task.CompletedTask;
        }

        public Task InitSynchronization()
        {
            if (_nethermindApi is not null)
            {
                _nethermindApi.BetterPeerStrategy = new AuRaBetterPeerStrategy(_nethermindApi.BetterPeerStrategy!, _nethermindApi.LogManager);
            }

            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi is not null)
            {
                return _blockProducerStarter!.BuildProducer(additionalTxSource);
            }

            return null;
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            return new StandardBlockProducerRunner(
                _blockProducerStarter.CreateTrigger(),
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

        public IModule Module => new AuraModule();
    }

    public class AuraModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder.AddSingleton<NethermindApi, AuRaNethermindApi>();
        }
    }
}
