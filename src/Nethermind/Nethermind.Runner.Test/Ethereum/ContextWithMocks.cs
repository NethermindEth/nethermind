// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
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
using Nethermind.Specs;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static NethermindApi ContextWithMocks() =>
            new NethermindApi()
            {
                LogManager = LimboLogs.Instance,
                Enode = Substitute.For<IEnode>(),
                TxPool = Substitute.For<ITxPool>(),
                Wallet = Substitute.For<IWallet>(),
                BlockTree = Substitute.For<IBlockTree>(),
                SyncServer = Substitute.For<ISyncServer>(),
                DbProvider = TestMemDbProvider.Init(),
                PeerManager = Substitute.For<IPeerManager>(),
                PeerPool = Substitute.For<IPeerPool>(),
                SpecProvider = Substitute.For<ISpecProvider>(),
                EthereumEcdsa = Substitute.For<IEthereumEcdsa>(),
                MainBlockProcessor = Substitute.For<IBlockProcessor>(),
                ReceiptStorage = Substitute.For<IReceiptStorage>(),
                ReceiptFinder = Substitute.For<IReceiptFinder>(),
                BlockValidator = Substitute.For<IBlockValidator>(),
                RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>(),
                TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>(),
                StaticNodesManager = Substitute.For<IStaticNodesManager>(),
                BloomStorage = Substitute.For<IBloomStorage>(),
                Sealer = Substitute.For<ISealer>(),
                Synchronizer = Substitute.For<ISynchronizer>(),
                BlockchainProcessor = Substitute.For<IBlockchainProcessor>(),
                BlockProducer = Substitute.For<IBlockProducer>(),
                ConfigProvider = Substitute.For<IConfigProvider>(),
                DiscoveryApp = Substitute.For<IDiscoveryApp>(),
                EngineSigner = Substitute.For<ISigner>(),
                FileSystem = Substitute.For<IFileSystem>(),
                FilterManager = Substitute.For<IFilterManager>(),
                FilterStore = Substitute.For<IFilterStore>(),
                GrpcServer = Substitute.For<IGrpcServer>(),
                HeaderValidator = Substitute.For<IHeaderValidator>(),
                IpResolver = Substitute.For<IIPResolver>(),
                KeyStore = Substitute.For<IKeyStore>(),
                LogFinder = Substitute.For<ILogFinder>(),
                MonitoringService = Substitute.For<IMonitoringService>(),
                ProtocolsManager = Substitute.For<IProtocolsManager>(),
                ProtocolValidator = Substitute.For<IProtocolValidator>(),
                RlpxPeer = Substitute.For<IRlpxHost>(),
                SealValidator = Substitute.For<ISealValidator>(),
                SessionMonitor = Substitute.For<ISessionMonitor>(),
                SnapProvider = Substitute.For<ISnapProvider>(),
                StateProvider = Substitute.For<IStateProvider>(),
                StateReader = Substitute.For<IStateReader>(),
                StorageProvider = Substitute.For<IStorageProvider>(),
                TransactionProcessor = Substitute.For<ITransactionProcessor>(),
                TxSender = Substitute.For<ITxSender>(),
                BlockProcessingQueue = Substitute.For<IBlockProcessingQueue>(),
                EngineSignerStore = Substitute.For<ISignerStore>(),
                EthereumJsonSerializer = Substitute.For<IJsonSerializer>(),
                NodeStatsManager = Substitute.For<INodeStatsManager>(),
                RpcModuleProvider = Substitute.For<IRpcModuleProvider>(),
                SyncModeSelector = Substitute.For<ISyncModeSelector>(),
                SyncPeerPool = Substitute.For<ISyncPeerPool>(),
                PeerDifficultyRefreshPool = Substitute.For<IPeerDifficultyRefreshPool>(),
                WebSocketsManager = Substitute.For<IWebSocketsManager>(),
                ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>(),
                TrieStore = Substitute.For<ITrieStore>(),
                ReadOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>(),
                ReadOnlyStorageTrieStore = Substitute.For<IReadOnlyTrieStore>(),
                ChainSpec = new ChainSpec(),
                BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>(),
                TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>(),
                GasPriceOracle = Substitute.For<IGasPriceOracle>(),
                EthSyncingInfo = Substitute.For<IEthSyncingInfo>(),
                HealthHintService = Substitute.For<IHealthHintService>(),
                TxValidator = new TxValidator(MainnetSpecProvider.Instance.ChainId),
                UnclesValidator = Substitute.For<IUnclesValidator>(),
                BlockProductionPolicy = Substitute.For<IBlockProductionPolicy>(),
                SyncProgressResolver = Substitute.For<ISyncProgressResolver>(),
                BetterPeerStrategy = Substitute.For<IBetterPeerStrategy>(),
                ReceiptMonitor = Substitute.For<IReceiptMonitor>(),
                WitnessRepository = Substitute.For<IWitnessRepository>()
            };
    }
}
