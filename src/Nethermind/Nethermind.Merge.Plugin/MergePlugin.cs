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
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Handlers.V1;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusWrapperPlugin, ISynchronizationPlugin
    {
        private INethermindApi _api = null!;
        private ILogger _logger = null!;
        private IMergeConfig _mergeConfig = null!;
        private ISyncConfig _syncConfig = null!;
        private IPoSSwitcher _poSSwitcher = NoPoS.Instance;
        private IBeaconPivot? _beaconPivot;
        private BeaconSync? _beaconSync;

        private ManualBlockFinalizationManager _blockFinalizationManager = null!;

        public string Name => "Merge";
        public string Description => "Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            _syncConfig = nethermindApi.Config<ISyncConfig>();
            _logger = _api.LogManager.GetClassLogger();

            if (_mergeConfig.Enabled)
            {
                if (_api.DbProvider == null) throw new ArgumentException(nameof(_api.DbProvider));
                if (_api.BlockTree == null) throw new ArgumentException(nameof(_api.BlockTree));
                if (_api.SpecProvider == null) throw new ArgumentException(nameof(_api.SpecProvider));
                if (_api.ChainSpec == null) throw new ArgumentException(nameof(_api.ChainSpec));
                

                _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.LogManager);
                _poSSwitcher = new PoSSwitcher(_mergeConfig,
                    _api.DbProvider.GetDb<IDb>(DbNames.Metadata), _api.BlockTree, _api.SpecProvider, _api.LogManager);
                _blockFinalizationManager = new ManualBlockFinalizationManager();

                Address feeRecipient;
                if (string.IsNullOrWhiteSpace(_mergeConfig.FeeRecipient))
                {
                    feeRecipient = Address.Zero;
                    if (_logger.IsInfo) _logger.Info("FeeRecipient will be set based on PayloadAttributes.SuggestedFeeRecipient field from CL");
                }
                else
                {
                    feeRecipient = new Address(_mergeConfig.FeeRecipient);
                    if (_logger.IsInfo) _logger.Info($"FeeRecipient: {feeRecipient}");
                }

                _api.RewardCalculatorSource = new MergeRewardCalculatorSource(
                   _api.RewardCalculatorSource ?? NoBlockRewards.Instance,  _poSSwitcher);
                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, feeRecipient, _api.LogManager);
                _api.SealValidator = _api.SealEngine;
                _api.Sealer = _api.SealEngine;
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
                

                _api.HeaderValidator = new PostMergeHeaderValidator(_poSSwitcher, _api.BlockTree, _api.SpecProvider, _api.SealEngine, _api.LogManager);
                _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
                _api.BlockValidator = new BlockValidator(_api.TxValidator, _api.HeaderValidator, _api.UnclesValidator,
                    _api.SpecProvider, _api.LogManager);
                _api.HealthHintService =
                    new MergeHealthHintService(_api.HealthHintService, _poSSwitcher);

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
                if (_api.BlockProcessingQueue is null)
                    throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_beaconPivot is null) throw new ArgumentNullException(nameof(_beaconPivot));
                if (_beaconSync is null) throw new ArgumentNullException(nameof(_beaconSync));
                
                ISyncConfig? syncConfig = _api.Config<ISyncConfig>();
                PayloadService payloadService = new (_idealBlockProductionContext, _api.Sealer, _mergeConfig, _api.LogManager);
                
                IEngineRpcModule engineRpcModule = new EngineRpcModule(
                    new GetPayloadV1Handler(payloadService, _api.LogManager),
                    new NewPayloadV1Handler(
                        _api.BlockValidator,
                        _api.BlockTree,
                        _api.BlockchainProcessor,
                        _api.EthSyncingInfo,
                        _api.Config<IInitConfig>(),
                        _poSSwitcher,
                        _beaconSync,
                        _beaconPivot,
                        _api.LogManager),
                    new ForkchoiceUpdatedV1Handler(
                        _api.BlockTree,
                        _blockFinalizationManager,
                        _poSSwitcher,
                        _api.EthSyncingInfo,
                        _api.BlockConfirmationManager,
                        payloadService,
                        _beaconSync,
                        _api.LogManager),
                    new ExecutionStatusHandler(_api.BlockTree, _api.BlockConfirmationManager,
                        _blockFinalizationManager),
                    new GetPayloadBodiesV1Handler(_api.BlockTree, _api.LogManager),
                    _api.LogManager);

                _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
                if (_logger.IsInfo) _logger.Info("Engine Module has been enabled");
            }

            return Task.CompletedTask;
        }

        public Task InitSynchronization()
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.SyncPeerPool is null) throw new ArgumentNullException(nameof(_api.SyncPeerPool));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.DbProvider is null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_beaconPivot is null) throw new ArgumentNullException(nameof(_beaconPivot));
                if (_api.SyncProgressResolver is null)
                    throw new ArgumentNullException(nameof(_api.SyncProgressResolver));
                _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _api.SyncProgressResolver);

                // ToDo strange place for validators initialization
                _api.HeaderValidator = new PostMergeHeaderValidator(_poSSwitcher, _api.BlockTree, _api.SpecProvider,
                    Always.Valid, _api.LogManager);
                _api.BlockValidator = new BlockValidator(_api.TxValidator, _api.HeaderValidator, Always.Valid,
                    _api.SpecProvider, _api.LogManager);
                
                _api.SyncModeSelector = new MultiSyncModeSelector(_api.SyncProgressResolver, _api.SyncPeerPool,
                    _syncConfig,
                    _beaconSync, _api.LogManager);
                _api.Pivot = _beaconPivot;
                _api.BlockDownloaderFactory = new MergeBlockDownloaderFactory(_beaconPivot, _api.SpecProvider,
                    _api.BlockTree,
                    _api.ReceiptStorage!,
                    _api.BlockValidator!,
                    _api.SealValidator!,
                    _api.SyncPeerPool,
                    _api.NodeStatsManager!,
                    _api.SyncModeSelector!,
                    _syncConfig,
                    _api.LogManager);
                _api.Synchronizer = new MergeSynchronizer(
                    _api.DbProvider, 
                    _api.SpecProvider!,
                    _api.BlockTree!,
                    _api.ReceiptStorage!,
                    _api.SyncPeerPool,
                    _api.NodeStatsManager!,
                    _api.SyncModeSelector,
                    _syncConfig,
                    _api.BlockDownloaderFactory,
                    _api.Pivot,
                    _api.LogManager);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string SealEngineType => "Eth2Merge";
    }
}
