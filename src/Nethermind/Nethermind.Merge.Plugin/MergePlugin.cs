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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Handlers.V1;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin : IConsensusWrapperPlugin, ISynchronizationPlugin
    {
        protected INethermindApi _api = null!;
        private ILogger _logger = null!;
        protected IMergeConfig _mergeConfig = null!;
        private ISyncConfig _syncConfig = null!;
        protected IPoSSwitcher _poSSwitcher = NoPoS.Instance;
        private IBeaconPivot? _beaconPivot;
        private BeaconSync? _beaconSync;
        private IBlockCacheService _blockCacheService = null!;
        private InvalidChainTracker.InvalidChainTracker _invalidChainTracker = null!;
        private IPeerRefresher _peerRefresher = null!;

        private ManualBlockFinalizationManager _blockFinalizationManager = null!;
        private IMergeBlockProductionPolicy? _mergeBlockProductionPolicy;

        public string Name => "Merge";
        public string Description => "Merge plugin for ETH1-ETH2";
        public string Author => "Nethermind";

        public virtual bool MergeEnabled => _mergeConfig.Enabled &&
                                            !IsPreMergeConsensusAuRa(_api); // AuRa has dedicated plugin AuRaMergePlugin
        protected bool IsPreMergeConsensusAuRa(INethermindApi api)
        {
            return api.ChainSpec?.SealEngineType == SealEngineType.AuRa;
        }

        // Don't remove default constructor. It is used by reflection when we're loading plugins
        public MergePlugin() { }

        public virtual Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = nethermindApi.Config<IMergeConfig>();
            _syncConfig = nethermindApi.Config<ISyncConfig>();
            _logger = _api.LogManager.GetClassLogger();

            if (MergeEnabled)
            {
                if (_api.DbProvider == null) throw new ArgumentException(nameof(_api.DbProvider));
                if (_api.BlockTree == null) throw new ArgumentException(nameof(_api.BlockTree));
                if (_api.SpecProvider == null) throw new ArgumentException(nameof(_api.SpecProvider));
                if (_api.ChainSpec == null) throw new ArgumentException(nameof(_api.ChainSpec));
                if (_api.SealValidator == null) throw new ArgumentException(nameof(_api.SealValidator));

                EnsureJsonRpcUrl();
                EnsureReceiptAvailable();

                _blockCacheService = new BlockCacheService();
                _poSSwitcher = new PoSSwitcher(
                    _mergeConfig,
                    _syncConfig,
                    _api.DbProvider.GetDb<IDb>(DbNames.Metadata),
                    _api.BlockTree,
                    _api.SpecProvider,
                    _api.LogManager);
                _invalidChainTracker = new InvalidChainTracker.InvalidChainTracker(
                    _poSSwitcher,
                    _api.BlockTree,
                    _blockCacheService,
                    _api.LogManager);
                _api.DisposeStack.Push(_invalidChainTracker);
                _blockFinalizationManager = new ManualBlockFinalizationManager();

                _api.RewardCalculatorSource = new MergeRewardCalculatorSource(
                   _api.RewardCalculatorSource ?? NoBlockRewards.Instance, _poSSwitcher);
                _api.SealValidator = new InvalidHeaderSealInterceptor(
                    new MergeSealValidator(_poSSwitcher, _api.SealValidator),
                    _invalidChainTracker,
                    _api.LogManager);

                _api.GossipPolicy = new MergeGossipPolicy(_api.GossipPolicy, _poSSwitcher, _blockCacheService);

                _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_poSSwitcher));

                FixTransitionBlock();
            }

            return Task.CompletedTask;
        }

        private void FixTransitionBlock()
        {
            // Special case during mainnet merge where if a transition block does not get processed through gossip
            // it does not get marked as main causing some issue on eth_getLogs.
            Keccak blockHash = new Keccak("0x55b11b918355b1ef9c5db810302ebad0bf2544255b530cdce90674d5887bb286");
            Block? block = _api.BlockTree!.FindBlock(blockHash);
            if (block != null)
            {
                ChainLevelInfo? level = _api.ChainLevelInfoRepository!.LoadLevel(block.Number);
                if (level == null)
                {
                    _logger.Warn("Unable to fix transition block. Unable to find chain level info.");
                    return;
                }

                int? index = level.FindBlockInfoIndex(blockHash);
                if (index is null)
                {
                    _logger.Warn("Unable to fix transition block. Missing block info for the transition block.");
                    return;
                }

                if (index.Value != 0)
                {
                    (level.BlockInfos[index.Value], level.BlockInfos[0]) = (level.BlockInfos[0], level.BlockInfos[index.Value]);
                    _api.ChainLevelInfoRepository.PersistLevel(block.Number, level);
                }

                _api.ReceiptStorage!.EnsureCanonical(block);
            }
        }

        private void EnsureReceiptAvailable()
        {
            if (HasTtd() == false) // by default we have Merge.Enabled = true, for chains that are not post-merge, we can skip this check, but we can still working with MergePlugin
                return;

            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            if (syncConfig.FastSync)
            {
                if (!syncConfig.DownloadReceiptsInFastSync || !syncConfig.DownloadBodiesInFastSync)
                {
                    throw new InvalidConfigurationException(
                        "Receipt and body must be available for merge to function. The following configs values should be set to true: Sync.DownloadReceiptsInFastSync, Sync.DownloadBodiesInFastSync",
                        ExitCodes.NoDownloadOldReceiptsOrBlocks);
                }
            }
        }

        private void EnsureJsonRpcUrl()
        {
            if (HasTtd() == false) // by default we have Merge.Enabled = true, for chains that are not post-merge, wwe can skip this check, but we can still working with MergePlugin
                return;

            IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                if (_logger.IsInfo)
                    _logger.Info("JsonRpc not enabled. Turning on JsonRpc URL with engine API.");

                jsonRpcConfig.Enabled = true;

                EnsureEngineModuleIsConfigured();

                if (!jsonRpcConfig.EnabledModules.Contains("engine"))
                {
                    // Disable it
                    jsonRpcConfig.EnabledModules = new string[] { };
                }

                jsonRpcConfig.AdditionalRpcUrls = jsonRpcConfig.AdditionalRpcUrls
                    .Where((url) => JsonRpcUrl.Parse(url).EnabledModules.Contains("engine"))
                    .ToArray();
            }
            else
            {
                EnsureEngineModuleIsConfigured();
            }
        }

        private void EnsureEngineModuleIsConfigured()
        {
            JsonRpcUrlCollection urlCollection = new(_api.LogManager, _api.Config<IJsonRpcConfig>(), false);
            bool hasEngineApiConfigured = urlCollection
                .Values
                .Any(rpcUrl => rpcUrl.EnabledModules.Contains("engine"));

            if (!hasEngineApiConfigured)
            {
                throw new InvalidConfigurationException(
                    "Engine module wasn't configured on any port. Nethermind can't work without engine port configured. Verify your RPC configuration. You can find examples in our docs: https://docs.nethermind.io/nethermind/ethereum-client/engine-jsonrpc-configuration-examples",
                    ExitCodes.NoEngineModule);
            }
        }

        private bool HasTtd()
        {
            return _api.SpecProvider?.TerminalTotalDifficulty != null || _mergeConfig.TerminalTotalDifficulty != null;
        }

        public Task InitNetworkProtocol()
        {
            if (MergeEnabled)
            {
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
                if (_api.BlockProductionPolicy == null) throw new ArgumentException(nameof(_api.BlockProductionPolicy));
                if (_api.SealValidator == null) throw new ArgumentException(nameof(_api.SealValidator));
                if (_api.HeaderValidator == null) throw new ArgumentException(nameof(_api.HeaderValidator));

                MergeHeaderValidator headerValidator = new(
                        _poSSwitcher,
                        _api.HeaderValidator,
                        _api.BlockTree,
                        _api.SpecProvider,
                        _api.SealValidator,
                        _api.LogManager);

                _api.HeaderValidator = new InvalidHeaderInterceptor(
                    headerValidator,
                    _invalidChainTracker,
                    _api.LogManager);

                _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
                _api.BlockValidator = new InvalidBlockInterceptor(
                    new BlockValidator(_api.TxValidator, _api.HeaderValidator, _api.UnclesValidator,
                        _api.SpecProvider, _api.LogManager),
                    _invalidChainTracker,
                    _api.LogManager);
                _api.HealthHintService =
                    new MergeHealthHintService(_api.HealthHintService, _poSSwitcher, _mergeConfig);
                _mergeBlockProductionPolicy = new MergeBlockProductionPolicy(_api.BlockProductionPolicy);
                _api.BlockProductionPolicy = _mergeBlockProductionPolicy;

                _api.FinalizationManager = new MergeFinalizationManager(_blockFinalizationManager, _api.FinalizationManager, _poSSwitcher);

                // Need to do it here because blockprocessor is not available in init
                _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor!);
            }

            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (MergeEnabled)
            {
                if (_api.RpcModuleProvider is null) throw new ArgumentNullException(nameof(_api.RpcModuleProvider));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.StateProvider is null) throw new ArgumentNullException(nameof(_api.StateProvider));
                if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_api.EthSyncingInfo is null) throw new ArgumentNullException(nameof(_api.EthSyncingInfo));
                if (_api.Sealer is null) throw new ArgumentNullException(nameof(_api.Sealer));
                if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.SyncProgressResolver is null) throw new ArgumentNullException(nameof(_api.SyncProgressResolver));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.StateReader is null) throw new ArgumentNullException(nameof(_api.StateReader));
                if (_beaconPivot is null) throw new ArgumentNullException(nameof(_beaconPivot));
                if (_beaconSync is null) throw new ArgumentNullException(nameof(_beaconSync));
                if (_blockProductionTrigger is null) throw new ArgumentNullException(nameof(_blockProductionTrigger));
                if (_peerRefresher is null) throw new ArgumentNullException(nameof(_peerRefresher));


                if (_postMergeBlockProducer is null) throw new ArgumentNullException(nameof(_postMergeBlockProducer));
                if (_blockProductionTrigger is null) throw new ArgumentNullException(nameof(_blockProductionTrigger));

                // ToDo: ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart. Then we will wait 5s more to ensure everything is processed
                while (!_api.BlockProcessingQueue.IsEmpty)
                {
                    Thread.Sleep(100);
                }
                Thread.Sleep(5000);

                IBlockImprovementContextFactory improvementContextFactory;
                if (string.IsNullOrEmpty(_mergeConfig.BuilderRelayUrl))
                {
                    improvementContextFactory = new BlockImprovementContextFactory(_blockProductionTrigger, TimeSpan.FromSeconds(_mergeConfig.SecondsPerSlot));
                }
                else
                {
                    DefaultHttpClient httpClient = new(new HttpClient(), _api.EthereumJsonSerializer, _api.LogManager, retryDelayMilliseconds: 100);
                    IBoostRelay boostRelay = new BoostRelay(httpClient, _mergeConfig.BuilderRelayUrl);
                    BoostBlockImprovementContextFactory boostBlockImprovementContextFactory = new(_blockProductionTrigger, TimeSpan.FromSeconds(_mergeConfig.SecondsPerSlot), boostRelay, _api.StateReader);
                    improvementContextFactory = boostBlockImprovementContextFactory;
                }

                PayloadPreparationService payloadPreparationService = new(
                    _postMergeBlockProducer,
                    improvementContextFactory,
                    _api.TimerFactory,
                    _api.LogManager,
                    TimeSpan.FromSeconds(_mergeConfig.SecondsPerSlot));

                IEngineRpcModule engineRpcModule = new EngineRpcModule(
                    new GetPayloadV1Handler(payloadPreparationService, _api.LogManager),
                    new NewPayloadV1Handler(
                        _api.BlockValidator,
                        _api.BlockTree,
                        _api.Config<IInitConfig>(),
                        _api.Config<ISyncConfig>(),
                        _poSSwitcher,
                        _beaconSync,
                        _beaconPivot,
                        _blockCacheService,
                        _api.BlockProcessingQueue,
                        _invalidChainTracker,
                        _beaconSync,
                        _api.SpecProvider,
                        _api.LogManager),
                    new ForkchoiceUpdatedV1Handler(
                        _api.BlockTree,
                        _blockFinalizationManager,
                        _poSSwitcher,
                        payloadPreparationService,
                        _api.BlockProcessingQueue,
                        _blockCacheService,
                        _invalidChainTracker,
                        _beaconSync,
                        _beaconPivot,
                        _peerRefresher,
                        _api.LogManager),
                    new ExecutionStatusHandler(_api.BlockTree),
                    new GetPayloadBodiesV1Handler(_api.BlockTree, _api.LogManager),
                    new ExchangeTransitionConfigurationV1Handler(_poSSwitcher, _api.LogManager),
                    _api.LogManager);

                _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
                if (_logger.IsInfo) _logger.Info("Engine Module has been enabled");
            }

            return Task.CompletedTask;
        }

        public Task InitSynchronization()
        {
            if (MergeEnabled)
            {
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.SyncPeerPool is null) throw new ArgumentNullException(nameof(_api.SyncPeerPool));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.DbProvider is null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.SyncProgressResolver is null) throw new ArgumentNullException(nameof(_api.SyncProgressResolver));
                if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_blockCacheService is null) throw new ArgumentNullException(nameof(_blockCacheService));
                if (_api.BetterPeerStrategy is null) throw new ArgumentNullException(nameof(_api.BetterPeerStrategy));
                if (_api.SealValidator is null) throw new ArgumentNullException(nameof(_api.SealValidator));
                if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
                if (_api.NodeStatsManager is null) throw new ArgumentNullException(nameof(_api.NodeStatsManager));
                if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_api.PeerDifficultyRefreshPool is null) throw new ArgumentNullException(nameof(_api.PeerDifficultyRefreshPool));

                // ToDo strange place for validators initialization
                PeerRefresher peerRefresher = new(_api.PeerDifficultyRefreshPool, _api.TimerFactory, _api.LogManager);
                _peerRefresher = peerRefresher;
                _api.DisposeStack.Push(peerRefresher);
                _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.LogManager);

                MergeHeaderValidator headerValidator = new(
                        _poSSwitcher,
                        _api.HeaderValidator,
                        _api.BlockTree,
                        _api.SpecProvider,
                        _api.SealValidator,
                        _api.LogManager);

                _api.HeaderValidator = new InvalidHeaderInterceptor(
                    headerValidator,
                    _invalidChainTracker,
                    _api.LogManager);

                _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
                _api.BlockValidator = new InvalidBlockInterceptor(
                    new BlockValidator(
                        _api.TxValidator,
                        _api.HeaderValidator,
                        _api.UnclesValidator,
                        _api.SpecProvider,
                        _api.LogManager),
                    _invalidChainTracker,
                    _api.LogManager);
                _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _api.LogManager);

                _api.BetterPeerStrategy = new MergeBetterPeerStrategy(_api.BetterPeerStrategy, _poSSwitcher, _beaconPivot, _api.LogManager);

                _api.SyncModeSelector = new MultiSyncModeSelector(
                    _api.SyncProgressResolver,
                    _api.SyncPeerPool,
                    _syncConfig,
                    _beaconSync,
                    _api.BetterPeerStrategy!,
                    _api.LogManager);
                _api.Pivot = _beaconPivot;

                SyncReport syncReport = new(_api.SyncPeerPool, _api.NodeStatsManager, _api.SyncModeSelector, _syncConfig, _beaconPivot, _api.LogManager);

                _api.BlockDownloaderFactory = new MergeBlockDownloaderFactory(
                    _poSSwitcher,
                    _beaconPivot,
                    _api.SpecProvider,
                    _api.BlockTree,
                    _blockCacheService,
                    _api.ReceiptStorage!,
                    _api.BlockValidator!,
                    _api.SealValidator!,
                    _api.SyncPeerPool,
                    _syncConfig,
                    _api.BetterPeerStrategy!,
                    syncReport,
                    _api.SyncProgressResolver,
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
                    _api.SnapProvider,
                    _api.BlockDownloaderFactory,
                    _api.Pivot,
                    _poSSwitcher,
                    _mergeConfig,
                    _invalidChainTracker,
                    _api.LogManager,
                    syncReport);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool MustInitialize { get => true; }
    }
}
