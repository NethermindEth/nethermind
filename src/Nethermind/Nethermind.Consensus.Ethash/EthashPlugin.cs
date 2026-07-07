// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class EthashPlugin(ChainSpec chainSpec, IMiningConfig miningConfig) : IConsensusPlugin
    {
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;


        public string SealEngineType => Core.SealEngineType.Ethash;

        public IModule Module => new EthHashModule(miningConfig);
    }

    public class EthHashModule(IMiningConfig miningConfig) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder
                .AddSingleton<IRewardCalculatorSource, RewardCalculator>()
                .AddSingleton<IDifficultyCalculator, EthashDifficultyCalculator>()
                .AddSingleton<IEthash, Ethash>()
                .AddSingleton<ISealValidator, EthashSealValidator>()

                .AddSingleton<EthashBlockProducerFactory>()
                .Bind<IBlockProducerFactory, EthashBlockProducerFactory>()
                .Bind<IBlockProducerRunnerFactory, EthashBlockProducerFactory>()
                ;

            if (miningConfig.Enabled)
            {
                builder.AddSingleton<ISealer, EthashSealer>();
            }
        }
    }
}
