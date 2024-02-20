// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa")]

namespace Nethermind.Consensus.AuRa
{
    /// <summary>
    /// Consensus plugin for AuRa setup.
    /// </summary>
    public class AuRaPlugin : IConsensusPlugin, ISynchronizationPlugin
    {
        private AuRaNethermindApi? _nethermindApi;
        private readonly ChainSpec _chainSpec;
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";
        public bool Enabled => _chainSpec.SealEngineType == SealEngineType;

        public string SealEngineType => Core.SealEngineType.AuRa;

        public AuRaPlugin(ChainSpec chainSpec)
        {
            _chainSpec = chainSpec;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi as AuRaNethermindApi;
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
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

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi is not null)
            {
                StartBlockProducerAuRa blockProducerStarter = new(_nethermindApi);
                DefaultBlockProductionTrigger ??= blockProducerStarter.CreateTrigger();
                return blockProducerStarter.BuildProducer(blockProductionTrigger ?? DefaultBlockProductionTrigger, additionalTxSource);
            }

            return Task.FromResult<IBlockProducer>(null);
        }

        public IBlockProductionTrigger? DefaultBlockProductionTrigger { get; private set; }

        public IModule? Module => new AuraModule();

        public class AuraModule : Module
        {
            protected override void Load(ContainerBuilder builder)
            {
                base.Load(builder);

                builder.RegisterType<AuRaNethermindApi>()
                    .AsSelf()
                    .As<INethermindApi>()
                    .SingleInstance();

                builder.RegisterDecorator<IGasLimitCalculator>((ctx, _, baseGasLimit) =>
                {
                    // So aura does a strange thing where the gas limit calculator is replaced later on. Not sure exactly
                    // why gas limit calculator is normally declared very early on. In any case, since its gas limit
                    // calculator is very complicated, it can't be resolved until more of the stack is migrated to DI.
                    AuRaNethermindApi api = ctx.Resolve<AuRaNethermindApi>();
                    if (api.AuraGasLimitCalculator != null) return api.AuraGasLimitCalculator;
                    return baseGasLimit;
                });

                builder.RegisterIStepsFromAssembly(GetType().Assembly);
            }
        }
    }
}
