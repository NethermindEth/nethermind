//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
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
            
            setInApi.Sealer = getFromApi.Config<IMiningConfig>().Enabled
                ? (ISealer) new EthashSealer(ethash, getFromApi.EngineSigner, getFromApi.LogManager)
                : NullSealEngine.Instance;
            setInApi.SealValidator = new EthashSealValidator(
                getFromApi.LogManager, difficultyCalculator, getFromApi.CryptoRandom, ethash);

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
