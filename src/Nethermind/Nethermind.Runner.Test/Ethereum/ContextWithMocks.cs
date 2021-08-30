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

using System.IO.Abstractions;
using Nethermind.Api;
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
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Monitoring;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.Runner.Ethereum.Api;
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
                RlpxPeer = Substitute.For<IRlpxPeer>(),
                SealValidator = Substitute.For<ISealValidator>(),
                SessionMonitor = Substitute.For<ISessionMonitor>(),
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
                WebSocketsManager = Substitute.For<IWebSocketsManager>(),
                ChainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>(),
                TrieStore = Substitute.For<ITrieStore>(),
                ReadOnlyTrieStore = Substitute.For<IReadOnlyTrieStore>(),
                ChainSpec = new ChainSpec(),
                BlockProducerEnvFactory = Substitute.For<IBlockProducerEnvFactory>(),
                TransactionComparerProvider = Substitute.For<ITransactionComparerProvider>()
            };
    }
}
