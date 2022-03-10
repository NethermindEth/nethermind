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
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Handlers.V1;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusWrapperPlugin
    {
        private INethermindApi _api = null!;
        private ILogger _logger = null!;
        private IMergeConfig _mergeConfig = null!;
        private IPoSSwitcher _poSSwitcher = NoPoS.Instance;
        private ManualBlockFinalizationManager _blockFinalizationManager = null!;
        private IMergeBlockProductionPolicy? _mergeBlockProductionPolicy;

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
                if (_api.DbProvider == null) throw new ArgumentException(nameof(_api.DbProvider));
                if (_api.BlockTree == null) throw new ArgumentException(nameof(_api.BlockTree));
                if (_api.SpecProvider == null) throw new ArgumentException(nameof(_api.SpecProvider));
                if (_api.ChainSpec == null) throw new ArgumentException(nameof(_api.ChainSpec));
                if (_api.SealValidator == null) throw new ArgumentException(nameof(_api.SealValidator));


                _poSSwitcher = new PoSSwitcher(_mergeConfig,
                    _api.DbProvider.GetDb<IDb>(DbNames.Metadata), _api.BlockTree, _api.SpecProvider, _api.LogManager);
                _blockFinalizationManager = new ManualBlockFinalizationManager();
                
                _api.RewardCalculatorSource = new MergeRewardCalculatorSource(
                   _api.RewardCalculatorSource ?? NoBlockRewards.Instance,  _poSSwitcher);
                _api.SealValidator = new MergeSealValidator(_poSSwitcher, _api.SealValidator);

                _api.GossipPolicy = new MergeGossipPolicy(_api.GossipPolicy, _poSSwitcher, _blockFinalizationManager);
                
                _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_poSSwitcher));
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
                if (_api.BlockProductionPolicy == null) throw new ArgumentException(nameof(_api.BlockProductionPolicy));
                if (_api.SealValidator == null) throw new ArgumentException(nameof(_api.SealValidator));
                
                _api.HeaderValidator = new MergeHeaderValidator(_poSSwitcher, _api.BlockTree, _api.SpecProvider, _api.SealValidator, _api.LogManager);
                _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
                _api.BlockValidator = new BlockValidator(_api.TxValidator, _api.HeaderValidator, _api.UnclesValidator,
                    _api.SpecProvider, _api.LogManager);
                _api.HealthHintService =
                    new MergeHealthHintService(_api.HealthHintService, _poSSwitcher);
                _mergeBlockProductionPolicy = new MergeBlockProductionPolicy(_api.BlockProductionPolicy);
                _api.BlockProductionPolicy = _mergeBlockProductionPolicy;

                _api.FinalizationManager = new MergeFinalizationManager(_blockFinalizationManager, _api.FinalizationManager, _poSSwitcher);
            }

            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.RpcModuleProvider is null) throw new ArgumentNullException(nameof(_api.RpcModuleProvider));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));
                if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_api.EthSyncingInfo is null) throw new ArgumentNullException(nameof(_api.EthSyncingInfo));
                if (_api.Sealer is null) throw new ArgumentNullException(nameof(_api.Sealer));
                if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_postMergeBlockProducer is null) throw new ArgumentNullException(nameof(_postMergeBlockProducer));
                if (_blockProductionTrigger is null) throw new ArgumentNullException(nameof(_blockProductionTrigger));
                
                ISyncConfig? syncConfig = _api.Config<ISyncConfig>();
                PayloadPreparationService payloadPreparationService = new (_postMergeBlockProducer, _blockProductionTrigger, _api.Sealer, _mergeConfig, TimerFactory.Default, _api.LogManager);
                
                IEngineRpcModule engineRpcModule = new EngineRpcModule(
                    new GetPayloadV1Handler(payloadPreparationService, _api.LogManager),
                    new NewPayloadV1Handler(
                        _api.BlockValidator,
                        _api.BlockTree,
                        _api.BlockchainProcessor,
                        _api.EthSyncingInfo,
                        _api.Config<IInitConfig>(),
                        _poSSwitcher,
                        _api.Synchronizer!,
                        syncConfig,
                        _api.LogManager),
                    new ForkchoiceUpdatedV1Handler(
                        _api.BlockTree,
                        _blockFinalizationManager,
                        _poSSwitcher,
                        _api.EthSyncingInfo,
                        payloadPreparationService,
                        _api.Synchronizer,
                        syncConfig, 
                        _api.LogManager),
                    new ExecutionStatusHandler(_api.BlockTree, _api.BlockConfirmationManager,
                        _blockFinalizationManager),
                    new GetPayloadBodiesV1Handler(_api.BlockTree, _api.LogManager),
                    new ExchangeTransitionConfigurationV1Handler(_poSSwitcher, _api.LogManager),
                    _api.LogManager);

                _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
                if (_logger.IsInfo) _logger.Info("Consensus Module has been enabled");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string SealEngineType => "Eth2Merge";
    }
}
