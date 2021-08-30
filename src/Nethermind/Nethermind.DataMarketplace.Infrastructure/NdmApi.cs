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

using System.Collections.Generic;
using System.IO.Abstractions;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.PubSub;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Consumers.Shared;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo;
using Nethermind.DataMarketplace.Infrastructure.Updaters;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.Infrastructure
{
    public class NdmApi : INdmApi
    {
        private INethermindApi _nethermindApi;

        public NdmApi(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
        }

        public IConfigManager? ConfigManager { get; set; }
        public IEthRequestService? EthRequestService { get; set; }
        public INdmFaucet? NdmFaucet { get; set; }
        public Address? ContractAddress { get; set; }
        public Address? ConsumerAddress { get; set; }
        public Address? ProviderAddress { get; set; }
        public IConsumerService ConsumerService { get; set; }
        public IAccountService AccountService { get; set; }
        public IRlpDecoder<DataAsset>? DataAssetRlpDecoder { get; set;}
        public IDepositService? DepositService { get; set;}
        public GasPriceService? GasPriceService { get; set;}
        public TransactionService? TransactionService { get; set;}
        public INdmNotifier? NdmNotifier { get; set;}
        public INdmAccountUpdater NdmAccountUpdater { get; set; }
        public INdmDataPublisher? NdmDataPublisher { get; set;}
        public IJsonRpcNdmConsumerChannel? JsonRpcNdmConsumerChannel { get; set;}
        public INdmConsumerChannelManager? NdmConsumerChannelManager { get; set;}
        public INdmBlockchainBridge? BlockchainBridge { get; set;}
        public IHttpClient? HttpClient { get; set; }
        public IMongoProvider? MongoProvider { get; set; }
        public IDbProvider? RocksProvider { get; set; }
        public IEthJsonRpcClientProxy? EthJsonRpcClientProxy { get; set; }
        public IJsonRpcClientProxy? JsonRpcClientProxy { get; set; }
        
        public INdmConfig? NdmConfig { get; set; } // strange way of overriding NDM config
        public string BaseDbPath { get; set; } // strange way of adding NDM

        public IAbiEncoder AbiEncoder => _nethermindApi.AbiEncoder;

        public ChainSpec? ChainSpec
        {
            get => _nethermindApi.ChainSpec;
            set => _nethermindApi.ChainSpec = value;
        }

        public DisposableStack DisposeStack => _nethermindApi.DisposeStack;

        public IBlockchainProcessor? BlockchainProcessor
        {
            get => _nethermindApi.BlockchainProcessor;
            set => _nethermindApi.BlockchainProcessor = value;
        }

        public CompositeBlockPreprocessorStep BlockPreprocessor => _nethermindApi.BlockPreprocessor;

        public IBlockProcessingQueue? BlockProcessingQueue
        {
            get => _nethermindApi.BlockProcessingQueue;
            set => _nethermindApi.BlockProcessingQueue = value;
        }

        public IBlockProcessor? MainBlockProcessor
        {
            get => _nethermindApi.MainBlockProcessor;
            set => _nethermindApi.MainBlockProcessor = value;
        }

        public IBlockProducer? BlockProducer
        {
            get => _nethermindApi.BlockProducer;
            set => _nethermindApi.BlockProducer = value;
        }

        public IBlockTree? BlockTree
        {
            get => _nethermindApi.BlockTree;
            set => _nethermindApi.BlockTree = value;
        }

        public IBlockValidator? BlockValidator
        {
            get => _nethermindApi.BlockValidator;
            set => _nethermindApi.BlockValidator = value;
        }

        public IBloomStorage? BloomStorage
        {
            get => _nethermindApi.BloomStorage;
            set => _nethermindApi.BloomStorage = value;
        }

        public IChainLevelInfoRepository? ChainLevelInfoRepository
        {
            get => _nethermindApi.ChainLevelInfoRepository;
            set => _nethermindApi.ChainLevelInfoRepository = value;
        }

        public IConfigProvider ConfigProvider
        {
            get => _nethermindApi.ConfigProvider;
            set => _nethermindApi.ConfigProvider = value;
        }

        public ICryptoRandom CryptoRandom => _nethermindApi.CryptoRandom;

        public IDbProvider? DbProvider
        {
            get => _nethermindApi.DbProvider;
            set => _nethermindApi.DbProvider = value;
        }

        public IRocksDbFactory? RocksDbFactory
        {
            get => _nethermindApi.RocksDbFactory;
            set => _nethermindApi.RocksDbFactory = value;
        }

        public IMemDbFactory? MemDbFactory 
        {
            get => _nethermindApi.MemDbFactory;
            set => _nethermindApi.MemDbFactory = value;
        }

        public IDisconnectsAnalyzer? DisconnectsAnalyzer
        {
            get => _nethermindApi.DisconnectsAnalyzer;
            set => _nethermindApi.DisconnectsAnalyzer = value;
        }

        public IDiscoveryApp? DiscoveryApp
        {
            get => _nethermindApi.DiscoveryApp;
            set => _nethermindApi.DiscoveryApp = value;
        }

        public IEnode? Enode
        {
            get => _nethermindApi.Enode;
            set => _nethermindApi.Enode = value;
        }

        public IEthereumEcdsa? EthereumEcdsa
        {
            get => _nethermindApi.EthereumEcdsa;
            set => _nethermindApi.EthereumEcdsa = value;
        }

        public IFileSystem FileSystem
        {
            get => _nethermindApi.FileSystem;
            set => _nethermindApi.FileSystem = value;
        }

        public IFilterStore FilterStore
        {
            get => _nethermindApi.FilterStore;
            set => _nethermindApi.FilterStore = value;
        }
        
        public IFilterManager FilterManager
        {
            get => _nethermindApi.FilterManager;
            set => _nethermindApi.FilterManager = value;
        }
        
        public IGrpcServer? GrpcServer
        {
            get => _nethermindApi.GrpcServer;
            set => _nethermindApi.GrpcServer = value;
        }

        public IHeaderValidator? HeaderValidator
        {
            get => _nethermindApi.HeaderValidator;
            set => _nethermindApi.HeaderValidator = value;
        }

        public IManualBlockProductionTrigger ManualBlockProductionTrigger
        {
            get => _nethermindApi.ManualBlockProductionTrigger;
            set => _nethermindApi.ManualBlockProductionTrigger = value;
        }

        public IIPResolver? IpResolver
        {
            get => _nethermindApi.IpResolver;
            set => _nethermindApi.IpResolver = value;
        }
        
        public IJsonSerializer EthereumJsonSerializer         
        {
            get => _nethermindApi.EthereumJsonSerializer;
            set => _nethermindApi.EthereumJsonSerializer = value;
        }

        public IKeyStore? KeyStore
        {
            get => _nethermindApi.KeyStore;
            set => _nethermindApi.KeyStore = value;
        }

        public ILogFinder LogFinder
        {
            get => _nethermindApi.LogFinder; 
            set => _nethermindApi.LogFinder = value;
        }


        public ILogManager LogManager         
        {
            get => _nethermindApi.LogManager;
            set => _nethermindApi.LogManager = value;
        }
        
        public IKeyValueStoreWithBatching? MainStateDbWithCache
        {
            get => _nethermindApi.MainStateDbWithCache;
            set => _nethermindApi.MainStateDbWithCache = value;
        }
        
        public IMessageSerializationService MessageSerializationService => _nethermindApi.MessageSerializationService;

        public IMonitoringService MonitoringService
        {
            get => _nethermindApi.MonitoringService;
            set => _nethermindApi.MonitoringService = value;
        }

        public INodeStatsManager? NodeStatsManager
        {
            get => _nethermindApi.NodeStatsManager;
            set => _nethermindApi.NodeStatsManager = value;
        }

        public IPeerManager? PeerManager
        {
            get => _nethermindApi.PeerManager;
            set => _nethermindApi.PeerManager = value;
        }

        public IProtocolsManager? ProtocolsManager
        {
            get => _nethermindApi.ProtocolsManager;
            set => _nethermindApi.ProtocolsManager = value;
        }

        public IProtocolValidator? ProtocolValidator
        {
            get => _nethermindApi.ProtocolValidator;
            set => _nethermindApi.ProtocolValidator = value;
        }

        public IReceiptStorage? ReceiptStorage
        {
            get => _nethermindApi.ReceiptStorage;
            set => _nethermindApi.ReceiptStorage = value;
        }

        public IReceiptFinder? ReceiptFinder
        {
            get => _nethermindApi.ReceiptFinder;
            set => _nethermindApi.ReceiptFinder = value;
        }

        public IRewardCalculatorSource? RewardCalculatorSource
        {
            get => _nethermindApi.RewardCalculatorSource;
            set => _nethermindApi.RewardCalculatorSource = value;
        }

        public IRlpxPeer? RlpxPeer
        {
            get => _nethermindApi.RlpxPeer;
            set => _nethermindApi.RlpxPeer = value;
        }

        public IRpcModuleProvider? RpcModuleProvider
        {
            get => _nethermindApi.RpcModuleProvider;
            set => _nethermindApi.RpcModuleProvider = value;
        }

        public ISealer? Sealer
        {
            get => _nethermindApi.Sealer;
            set => _nethermindApi.Sealer = value;
        }

        public ISealValidator? SealValidator
        {
            get => _nethermindApi.SealValidator;
            set => _nethermindApi.SealValidator = value;
        }

        public ISigner? EngineSigner
        {
            get => _nethermindApi.EngineSigner;
            set => _nethermindApi.EngineSigner = value;
        }

        public ISignerStore? EngineSignerStore
        {
            get => _nethermindApi.EngineSignerStore;
            set => _nethermindApi.EngineSignerStore = value;
        }

        public string SealEngineType              
        {
            get => _nethermindApi.SealEngineType;
            set => _nethermindApi.SealEngineType = value;
        }

        public ISpecProvider? SpecProvider
        {
            get => _nethermindApi.SpecProvider;
            set => _nethermindApi.SpecProvider = value;
        }

        public ISyncModeSelector? SyncModeSelector
        {
            get => _nethermindApi.SyncModeSelector;
            set => _nethermindApi.SyncModeSelector = value;
        }

        public ISyncPeerPool? SyncPeerPool
        {
            get => _nethermindApi.SyncPeerPool;
            set => _nethermindApi.SyncPeerPool = value;
        }

        public ISynchronizer? Synchronizer
        {
            get => _nethermindApi.Synchronizer;
            set => _nethermindApi.Synchronizer = value;
        }

        public ISyncServer? SyncServer
        {
            get => _nethermindApi.SyncServer;
            set => _nethermindApi.SyncServer = value;
        }

        /// <summary>
        /// Can be used only for processing blocks, on all other contexts use <see cref="StateReader"/> or <see cref="ChainHeadStateProvider"/>.
        /// </summary>
        /// <remarks>
        /// DO NOT USE OUTSIDE OF PROCESSING BLOCK CONTEXT!
        /// </remarks>
        public IStateProvider? StateProvider
        {
            get => _nethermindApi.StateProvider;
            set => _nethermindApi.StateProvider = value;
        }

        public IReadOnlyStateProvider? ChainHeadStateProvider         
        {
            get => _nethermindApi.ChainHeadStateProvider;
            set => _nethermindApi.ChainHeadStateProvider = value;
        }

        public IStateReader? StateReader
        {
            get => _nethermindApi.StateReader;
            set => _nethermindApi.StateReader = value;
        }

        public IStorageProvider? StorageProvider
        {
            get => _nethermindApi.StorageProvider;
            set => _nethermindApi.StorageProvider = value;
        }

        public ISessionMonitor? SessionMonitor
        {
            get => _nethermindApi.SessionMonitor;
            set => _nethermindApi.SessionMonitor = value;
        }

        public IStaticNodesManager? StaticNodesManager
        {
            get => _nethermindApi.StaticNodesManager;
            set => _nethermindApi.StaticNodesManager = value;
        }

        public ITimestamper Timestamper => _nethermindApi.Timestamper;
        public ITimerFactory TimerFactory => _nethermindApi.TimerFactory;

        public ITransactionProcessor? TransactionProcessor
        {
            get => _nethermindApi.TransactionProcessor;
            set => _nethermindApi.TransactionProcessor = value;
        }
        
        public ITrieStore? TrieStore
        {
            get => _nethermindApi.TrieStore;
            set => _nethermindApi.TrieStore = value;
        }
        
        public IReadOnlyTrieStore? ReadOnlyTrieStore
        {
            get => _nethermindApi.ReadOnlyTrieStore;
            set => _nethermindApi.ReadOnlyTrieStore = value;
        }

        public ITxSender? TxSender
        {
            get => _nethermindApi.TxSender;
            set => _nethermindApi.TxSender = value;
        }

        public ITxPool? TxPool
        {
            get => _nethermindApi.TxPool;
            set => _nethermindApi.TxPool = value;
        }

        public ITxPoolInfoProvider? TxPoolInfoProvider
        {
            get => _nethermindApi.TxPoolInfoProvider;
            set => _nethermindApi.TxPoolInfoProvider = value;
        }

        public IWitnessRepository? WitnessRepository
        {
            get => _nethermindApi.WitnessRepository;
            set => _nethermindApi.WitnessRepository = value;
        }

        public IHealthHintService? HealthHintService        
        {
            get => _nethermindApi.HealthHintService;
            set => _nethermindApi.HealthHintService = value;
        }

        public TxValidator? TxValidator
        {
            get => _nethermindApi.TxValidator; 
            set => _nethermindApi.TxValidator = value;
        }

        public IBlockFinalizationManager? FinalizationManager
        {
            get => _nethermindApi.FinalizationManager;
            set => _nethermindApi.FinalizationManager = value;
        }
        
        public IGasLimitCalculator GasLimitCalculator
        {
            get => _nethermindApi.GasLimitCalculator;
            set => _nethermindApi.GasLimitCalculator = value;
        }
        
        public IBlockProducerEnvFactory BlockProducerEnvFactory
        {
            get => _nethermindApi.BlockProducerEnvFactory;
            set => _nethermindApi.BlockProducerEnvFactory = value;
        }

        public IWallet? Wallet
        {
            get => _nethermindApi.Wallet;
            set => _nethermindApi.Wallet = value;
        }

        public ITransactionComparerProvider TransactionComparerProvider
        {
            get => _nethermindApi.TransactionComparerProvider;
            set => _nethermindApi.TransactionComparerProvider = value;
        }

        public IWebSocketsManager? WebSocketsManager
        {
            get => _nethermindApi.WebSocketsManager;
            set => _nethermindApi.WebSocketsManager = value;
        }

        public IWitnessCollector? WitnessCollector
        {
            get => _nethermindApi.WitnessCollector;
            set => _nethermindApi.WitnessCollector = value;
        }

        public ProtectedPrivateKey? NodeKey
        {
            get => _nethermindApi.NodeKey;
            set => _nethermindApi.NodeKey = value;
        }

        public ProtectedPrivateKey? OriginalSignerKey
        {
            get => _nethermindApi.OriginalSignerKey;
            set => _nethermindApi.OriginalSignerKey = value;
        }
        
        public IList<IPublisher> Publishers => _nethermindApi.Publishers;
        
        public IReadOnlyList<INethermindPlugin> Plugins => _nethermindApi.Plugins;

        public IBlockchainBridge CreateBlockchainBridge()
        {
            return _nethermindApi.CreateBlockchainBridge();
        }
    }
}
