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

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusPlugin
    {
        private INethermindApi _api = null!;
        private ILogger _logger = null!;
        private IMergeConfig _mergeConfig = null!;
        
        public string Name => "Merge";
        public string Description => "Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";
        
        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            _logger = _api.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_mergeConfig.Enabled)
            {
                _api.Config<ISyncConfig>().SynchronizationEnabled = false;
            }
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.RpcModuleProvider is null) throw new StepDependencyException(nameof(_api.RpcModuleProvider));
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
                if (_api.BlockchainProcessor is null) throw new StepDependencyException(nameof(_api.BlockchainProcessor));
                if (_api.StateProvider is null) throw new StepDependencyException(nameof(_api.StateProvider));
                if (_api.StateProvider is null) throw new StepDependencyException(nameof(_api.StateProvider));
                
                IConsensusRpcModule consensusRpcModule = new ConsensusRpcModule(
                    new AssembleBlockHandler(_api.BlockTree, _blockProducer, _api.LogManager),
                    new NewBlockHandler(_api.BlockTree, _api.BlockchainProcessor, _api.StateProvider, _api.LogManager),
                    new SetHeadBlockHandler(_api.BlockTree, _api.LogManager),
                    new FinaliseBlockHandler());
                
                _api.RpcModuleProvider.RegisterSingle<IConsensusRpcModule>(consensusRpcModule);
                if (_logger.IsInfo) _logger.Info("Consensus Module has been enabled");
            }

            return Task.CompletedTask;
        }
        
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public SealEngineType SealEngineType => SealEngineType.Custom;
        
    }
}
