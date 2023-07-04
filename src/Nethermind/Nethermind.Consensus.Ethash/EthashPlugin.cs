// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Consensus.Ethash
{
    public class EthashPlugin : IConsensusPlugin
    {
        private INethermindApi _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "Ethash";

        public string Description => "Ethash Consensus";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Ethash)
            {
                return Task.CompletedTask;
            }

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

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            return Task.FromResult((IBlockProducer)null);
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.Ethash;

        public IBlockProductionTrigger DefaultBlockProductionTrigger => _nethermindApi.ManualBlockProductionTrigger;
    }
}
