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

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Evm;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Ethereum
{
    public class EthereumRunnerContext
    {
        public T Config<T>() where T : IConfig
        {
            return ConfigProvider.GetConfig<T>();
        }
        
        public readonly Stack<IDisposable> DisposeStack = new Stack<IDisposable>();

        public List<IProducer> Producers = new List<IProducer>();
        public IGrpcServer GrpcServer;
        public ILogManager LogManager;
        public INdmConsumerChannelManager NdmConsumerChannelManager;
        public INdmDataPublisher NdmDataPublisher;
        public INdmInitializer NdmInitializer;
        public ILogger Logger;

        public IRpcModuleProvider RpcModuleProvider;
        public IConfigProvider ConfigProvider;
        public IIpResolver IpResolver;
        public PrivateKey NodeKey;
        public ChainSpec ChainSpec;
        public ICryptoRandom CryptoRandom = new CryptoRandom();
        public IJsonSerializer EthereumJsonSerializer;
        public CancellationTokenSource RunnerCancellation;
        public IBlockchainProcessor BlockchainProcessor;
        public IDiscoveryApp DiscoveryApp;
        public IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        public INodeStatsManager NodeStatsManager;
        public ITxPool TxPool;
        public IReceiptStorage ReceiptStorage;
        public IEthereumEcdsa EthereumEcdsa;
        public IEthSyncPeerPool SyncPeerPool;
        public ISynchronizer Synchronizer;
        public ISyncServer SyncServer;
        public IKeyStore KeyStore;
        public IPeerManager PeerManager;
        public IProtocolsManager ProtocolsManager;
        public IBlockTree BlockTree;
        public IBlockValidator BlockValidator;
        public IHeaderValidator HeaderValidator;
        public IBlockDataRecoveryStep RecoveryStep;
        public IBlockProcessor BlockProcessor;
        public IRewardCalculator RewardCalculator;
        public ISpecProvider SpecProvider;
        public IStateProvider StateProvider;
        public ISealer Sealer;
        public ISealValidator SealValidator;
        public IBlockProducer BlockProducer;
        public ISnapshotManager SnapshotManager;
        public IDbProvider DbProvider;
        public readonly ITimestamper Timestamper = Core.Timestamper.Default;
        public IStorageProvider StorageProvider;
        public IWallet Wallet;
        public IEnode Enode;
        public ISessionMonitor SessionMonitor;
        public IStaticNodesManager StaticNodesManager;
        public ITransactionProcessor TransactionProcessor;
        public ITxPoolInfoProvider TxPoolInfoProvider;
        public INetworkConfig NetworkConfig;
        public IChainLevelInfoRepository ChainLevelInfoRepository;
        public IBlockFinalizationManager FinalizationManager;
        public IBlockProcessingQueue BlockProcessingQueue;
        public IValidatorStore ValidatorStore;
        
        public IRlpxPeer RlpxPeer;
        public IWebSocketsManager WebSocketsManager;
        public IJsonRpcClientProxy JsonRpcClientProxy;
        public IEthJsonRpcClientProxy EthJsonRpcClientProxy;
        public IHttpClient HttpClient;
        public IMonitoringService MonitoringService;
    }
}