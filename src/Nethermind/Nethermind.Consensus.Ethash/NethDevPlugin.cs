// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public const string NethDev = "NethDev";
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => NethDev;

        public string Description => $"{NethDev} (Spaceneth)";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer()
        {
            var (getFromApi, _) = _nethermindApi!.ForProducer;

            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");

            IBlockProducerEnv env = getFromApi.BlockProducerEnvFactory.Create();
            IBlockProducer blockProducer = new DevBlockProducer(
                env.TxSource,
                env.ChainProcessor,
                env.ReadOnlyStateProvider,
                getFromApi.BlockTree,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IBlocksConfig>(),
                getFromApi.LogManager);

            return blockProducer;
        }

        public string SealEngineType => NethDev;

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(_nethermindApi.TxPool)
                .Or(_nethermindApi.ManualBlockProductionTrigger);
            return new StandardBlockProducerRunner(
                trigger,
                _nethermindApi.BlockTree,
                blockProducer);
        }

        public IModule Module => new NethDevPluginModule();

        private class NethDevPluginModule : Module
        {
            protected override void Load(ContainerBuilder builder)
            {
                base.Load(builder);

                builder
                    .AddSingleton<IBlockProducerTxSourceFactory, NethDevBlockProducerTxSourceFactory>()
                    ;
            }
        }
    }
}
