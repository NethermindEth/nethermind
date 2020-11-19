//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Consensus.Clique
{
    public class CliquePlugin : INethermindPlugin
    {
        public void Dispose()
        {
            throw new System.NotImplementedException();
        }

        public string Name => "Clique";
        public string Description => "Clique COnsensus Engine";
        public string Author => "Nethermind";

        private IBasicApi _basicApi;
        
        private IBlockchainApi _blockchainApi;

        private ISnapshotManager _snapshotManager;
        
        public Task Init(IBasicApi basicApi)
        {
            _basicApi = basicApi;
            return Task.CompletedTask;
        }
        
        public Task InitBlockchain(IBlockchainApi api)
        {
            _blockchainApi = api;
            _blockchainApi.RewardCalculatorSource = NoBlockRewards.Instance;
            CliqueConfig cliqueConfig = new CliqueConfig
            {
                BlockPeriod = _basicApi.ChainSpec.Clique.Period,
                Epoch = _basicApi.ChainSpec.Clique.Epoch
            };
            
            _snapshotManager = new SnapshotManager(
                cliqueConfig,
                _blockchainApi.DbProvider.BlocksDb,
                _blockchainApi.BlockTree,
                _basicApi.EthereumEcdsa,
                _basicApi.LogManager);
            
            api.SealValidator = new CliqueSealValidator(
                cliqueConfig,
                _snapshotManager,
                _basicApi.LogManager);
            
            // TODO: add
            api.RecoveryStep = new CompositeDataRecoveryStep(
                api.RecoveryStep, new AuthorRecoveryStep(_snapshotManager));
            
            if (_basicApi.Config<IInitConfig>().IsMining)
            {
                _blockchainApi.Sealer = new CliqueSealer(
                    _blockchainApi.EngineSigner,
                    cliqueConfig,
                    _snapshotManager,
                    _basicApi.LogManager);
            }
            else
            {
                _blockchainApi.Sealer = NullSealEngine.Instance;
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol(INetworkApi api)
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules(INethermindApi nethermindApi)
        {
            CliqueBridge cliqueBridge = new CliqueBridge(
                _blockchainApi.BlockProducer as ICliqueBlockProducer,
                _snapshotManager,
                nethermindApi.BlockTree);
            CliqueModule cliqueModule = new CliqueModule(_basicApi.LogManager, cliqueBridge);
            nethermindApi.RpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            
            return Task.CompletedTask;
        }
    }
}