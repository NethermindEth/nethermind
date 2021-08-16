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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Network.Config;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusPlugin
    {
        private INethermindApi _api = null!;
        private ILogger _logger = null!;
        private IMergeConfig _mergeConfig = null!;
        private ManualBlockFinalizationManager _blockFinalizationManager = null!;

        public string Name => "Merge";
        public string Description => "Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";
        
        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            _logger = _api.LogManager.GetClassLogger();

            if (_mergeConfig.Enabled)
            {
                if (string.IsNullOrEmpty(_mergeConfig.BlockAuthorAccount))
                {
                    if (_logger.IsError) _logger.Error($"{nameof(MergeConfig)}.{nameof(_mergeConfig.BlockAuthorAccount)} is not set up. Cannot create blocks. Stopping.");
                    Environment.Exit(13); // ERROR_INVALID_DATA
                }
                
                _api.EngineSigner = new Eth2Signer(new Address(_mergeConfig.BlockAuthorAccount));
                _api.RewardCalculatorSource = NoBlockRewards.Instance;
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_mergeConfig.Enabled)
            {
                ISyncConfig syncConfig = _api.Config<ISyncConfig>();
                syncConfig.SynchronizationEnabled = false;
                syncConfig.BlockGossipEnabled = false;
                _blockFinalizationManager = new ManualBlockFinalizationManager();
                _api.FinalizationManager = _blockFinalizationManager;
            }
            
            return Task.CompletedTask;
        }

        public async Task InitRpcModules()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.RpcModuleProvider is null) throw new ArgumentNullException(nameof(_api.RpcModuleProvider));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));
                
                await _api.BlockchainProcessor.StopAsync(true);

                _api.Config<IJsonRpcConfig>().EnableModules(ModuleType.Consensus);

                IConsensusRpcModule consensusRpcModule = new ConsensusRpcModule(
                    new AssembleBlockHandler(_api.BlockTree, _defaultBlockProductionTrigger, _manualTimestamper, _api.LogManager),
                    new NewBlockHandler(_api.BlockTree, _api.BlockPreprocessor, _api.BlockchainProcessor, _api.StateProvider, _api.Config<IInitConfig>(), _api.LogManager),
                    new SetHeadBlockHandler(_api.BlockTree, _api.StateProvider, _api.LogManager),
                    new FinaliseBlockHandler(_api.BlockTree, _blockFinalizationManager, _api.LogManager),
                    _api.LogManager);
                
                _api.RpcModuleProvider.RegisterSingle(consensusRpcModule);
                if (_logger.IsInfo) _logger.Info("Consensus Module has been enabled");
            }
        }
        
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string SealEngineType => "Eth2Merge";
    }
}
