// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class EthashPlugin(ChainSpec chainSpec) : IConsensusPlugin
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
            setInApi.RewardCalculatorSource = new RewardCalculator(getFromApi.SpecProvider);

            EthashDifficultyCalculator difficultyCalculator = new(getFromApi.SpecProvider);
            Ethash ethash = new(getFromApi.LogManager);

            bool miningEnabled = getFromApi.Config<IMiningConfig>()
                .Enabled;
            setInApi.Sealer = miningEnabled
                ? new EthashSealer(ethash, getFromApi.EngineSigner, getFromApi.LogManager)
                : NullSealEngine.Instance;
            setInApi.SealValidator = new EthashSealValidator(getFromApi.LogManager, difficultyCalculator, getFromApi.CryptoRandom, ethash, _nethermindApi.Timestamper);

            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
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
    }
}
