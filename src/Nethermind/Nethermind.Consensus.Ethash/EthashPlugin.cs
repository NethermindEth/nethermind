// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class EthashPlugin(ChainSpec chainSpec, IMiningConfig miningConfig) : IConsensusPlugin
    {
        private INethermindApi _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;

            var (getFromApi, setInApi) = _nethermindApi.ForInit;

            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer()
        {
            return null;
        }

        public string SealEngineType => Core.SealEngineType.Ethash;

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            return new StandardBlockProducerRunner(
                _nethermindApi.ManualBlockProductionTrigger,
                _nethermindApi.BlockTree,
                blockProducer);
        }

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
                ;

            if (miningConfig.Enabled)
            {
                builder.AddSingleton<ISealer, EthashSealer>();
            }
        }
    }
}
