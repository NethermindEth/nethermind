// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Container;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization;

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
        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        private StartBlockProducerAuRa? _blockProducerStarter;

        public IModule? ContainerModule => new AuraModule();

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

        public void ConfigureSynchronizationBuilder(ContainerBuilder containerBuilder)
        {
            if (_nethermindApi is not null)
            {
                containerBuilder.RegisterDecorator<AuRaBetterPeerStrategy, IBetterPeerStrategy>();
            }
        }

        public Task InitSynchronization()
        {
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

        public IBlockProducerRunner CreateBlockProducerRunner()
        {
            return new StandardBlockProducerRunner(
                _blockProducerStarter.CreateTrigger(),
                _nethermindApi.BlockTree,
                _nethermindApi.BlockProducer!);
        }
    }

    public class AuraModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .AddSingleton<INethermindApi, AuRaNethermindApi>()
                .AddIStepsFromAssembly(GetType().Assembly);
        }
    }
}
