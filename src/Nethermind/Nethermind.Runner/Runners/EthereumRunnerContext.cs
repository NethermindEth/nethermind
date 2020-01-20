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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
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
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunnerContext
    {
        public readonly Stack<IDisposable> _disposeStack = new Stack<IDisposable>();

        public bool HiveEnabled =
            Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";

        public IGrpcServer _grpcServer;
        public ILogManager LogManager;
        public INdmConsumerChannelManager _ndmConsumerChannelManager;
        public INdmDataPublisher _ndmDataPublisher;
        public INdmInitializer _ndmInitializer;
        public IWebSocketsManager _webSocketsManager;
        public ILogger Logger;

        public IRpcModuleProvider _rpcModuleProvider;
        public IConfigProvider _configProvider;
        public ITxPoolConfig _txPoolConfig;
        public IInitConfig _initConfig;
        public IIpResolver _ipResolver;
        public PrivateKey _nodeKey;
        public ChainSpec _chainSpec;
        public ICryptoRandom _cryptoRandom = new CryptoRandom();
        public IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        public IJsonSerializer _ethereumJsonSerializer;
        public IMonitoringService _monitoringService;
        public CancellationTokenSource _runnerCancellation;
        public IBlockchainProcessor _blockchainProcessor;
        public IDiscoveryApp _discoveryApp;
        public IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        public INodeStatsManager _nodeStatsManager;
        public ITxPool _txPool;
        public IReceiptStorage _receiptStorage;
        public IEthereumEcdsa _ethereumEcdsa;
        public IEthSyncPeerPool _syncPeerPool;
        public ISynchronizer _synchronizer;
        public ISyncServer _syncServer;
        public IKeyStore _keyStore;
        public IPeerManager PeerManager;
        public IProtocolsManager _protocolsManager;
        public IBlockTree BlockTree;
        public IBlockValidator _blockValidator;
        public IHeaderValidator _headerValidator;
        public IBlockDataRecoveryStep _recoveryStep;
        public IBlockProcessor _blockProcessor;
        public IRewardCalculator _rewardCalculator;
        public ISpecProvider SpecProvider;
        public IStateProvider _stateProvider;
        public ISealer _sealer;
        public ISealValidator _sealValidator;
        public IBlockProducer _blockProducer;
        public ISnapshotManager _snapshotManager;
        public IRlpxPeer _rlpxPeer;
        public IDbProvider _dbProvider;
        public readonly ITimestamper _timestamper = Timestamper.Default;
        public IStorageProvider _storageProvider;
        public IWallet _wallet;
        public IEnode _enode;
        public HiveRunner _hiveRunner;
        public ISessionMonitor _sessionMonitor;
        public ISyncConfig _syncConfig;
        public IStaticNodesManager _staticNodesManager;
        public ITransactionProcessor _transactionProcessor;
        public ITxPoolInfoProvider _txPoolInfoProvider;
        public INetworkConfig NetworkConfig;
        public IChainLevelInfoRepository _chainLevelInfoRepository;
        public IBlockFinalizationManager _finalizationManager;
        public IJsonRpcClientProxy _jsonRpcClientProxy;
        public IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        public IHttpClient _httpClient;
        public string DiscoveryNodesDbPath = "discoveryNodes";
        public string PeersDbPath = "peers";
    }
}