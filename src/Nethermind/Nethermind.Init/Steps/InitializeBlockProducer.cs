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

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork), typeof(ReviewBlockTree))]
    public class InitializeBlockProducer : IStep
    {
        protected IApiWithBlockchain _api;

        public InitializeBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockProductionPolicy.ShouldStartBlockProduction())
            {
                _api.BlockProducer = await BuildProducer();
            }
        }

        protected virtual async Task<IBlockProducer> BuildProducer()
        {
            _api.BlockProducerEnvFactory = new BlockProducerEnvFactory(_api.DbProvider,
                _api.BlockTree,
                _api.ReadOnlyTrieStore,
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource,
                _api.ReceiptStorage,
                _api.BlockPreprocessor,
                _api.TxPool,
                _api.TransactionComparerProvider,
                _api.Config<IMiningConfig>(),
                _api.LogManager);
            
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            IConsensusPlugin? consensusPlugin = _api.GetConsensusPlugin();
            
            if (consensusPlugin is not null)
            {
                foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins())
                {
                    return await wrapperPlugin.InitBlockProducer(consensusPlugin);
                }

                return await consensusPlugin.InitBlockProducer();
            }
            else
            {
                throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");    
            }
        }
    }
}
