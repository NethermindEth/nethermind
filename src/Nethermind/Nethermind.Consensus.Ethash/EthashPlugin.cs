// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.ApiBase;
using Nethermind.ApiBase.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class EthashPlugin : IConsensusPlugin, INethermindPlugin<INethermindApi>
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

        public IBasicApiWithPlugins CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer, ILogManager logManager, ChainSpec chainSpec) => new NethermindApi(configProvider, jsonSerializer, logManager, chainSpec);

        public string SealEngineType => Nethermind.Core.SealEngineType.Ethash;

        public IBlockProductionTrigger DefaultBlockProductionTrigger => _nethermindApi.ManualBlockProductionTrigger;
    }
}
