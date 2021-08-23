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
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaPlugin : IConsensusPlugin
    {
        private AuRaNethermindApi? _nethermindApi;
        public string Name => SealEngineType;

        public string Description => $"{SealEngineType} Consensus Engine";

        public string Author => "Nethermind";

        public string SealEngineType => Core.SealEngineType.AuRa;


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
        
        public INethermindApi CreateApi() => new AuRaNethermindApi();
    }
}
