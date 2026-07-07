// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public const string NethDev = "NethDev";

        public string Name => NethDev;

        public string Description => $"{NethDev} (Spaceneth)";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;


        public string SealEngineType => NethDev;

        public IModule Module => new NethDevPluginModule();

        private class NethDevPluginModule : Module
        {
            protected override void Load(ContainerBuilder builder)
            {
                base.Load(builder);

                builder
                    .AddSingleton<IBlockProducerTxSourceFactory, NethDevBlockProducerTxSourceFactory>()
                    .AddSingleton<NethDevBlockProducerFactory>()
                    .Bind<IBlockProducerFactory, NethDevBlockProducerFactory>()
                    .Bind<IBlockProducerRunnerFactory, NethDevBlockProducerFactory>()
                    ;
            }
        }
    }
}
