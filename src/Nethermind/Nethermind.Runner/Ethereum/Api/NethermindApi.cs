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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Ethereum.Api
{
    public class NethermindApi : INethermindApi
    {
        public T Config<T>() where T : IConfig
        {
            return ConfigProvider.GetConfig<T>();
        }

        public NethermindApi(IConfigProvider configProvider, ILogManager logManager)
        {
            ConfigProvider = configProvider;
            LogManager = logManager;
            
            CryptoRandom = new CryptoRandom();
            DisposeStack.Push(CryptoRandom);
        }
        
        public AbiEncoder AbiEncoder { get; } = new AbiEncoder();
        public ChainSpec? ChainSpec { get; set; }
        public DisposableStack DisposeStack { get; } = new DisposableStack();

        public IBlockchainProcessor? BlockchainProcessor { get; set; }
        public IBlockDataRecoveryStep? RecoveryStep { get; set; }
        public IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        public IBlockProcessor? MainBlockProcessor { get; set; }
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockTree? BlockTree { get; set; }
        public IBlockValidator? BlockValidator { get; set; }
        public IBloomStorage? BloomStorage { get; set; }
        public IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        public IConfigProvider ConfigProvider { get; set; }
        public ICryptoRandom CryptoRandom { get; }
        public IDbProvider? DbProvider { get; set; }
        public IDiscoveryApp? DiscoveryApp { get; set; }
        public IEnode? Enode { get; set; }
        public IEthereumEcdsa? EthereumEcdsa { get; set; }
        public IEthJsonRpcClientProxy? EthJsonRpcClientProxy;
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IGrpcServer? GrpcServer { get; set; }
        public IHeaderValidator? HeaderValidator { get; set; }
        public IHttpClient? HttpClient;
        public IIPResolver? IpResolver { get; set; }
        public IJsonRpcClientProxy? JsonRpcClientProxy;
        public IJsonSerializer? EthereumJsonSerializer { get; set; }
        public IKeyStore? KeyStore { get; set; }
        public ILogManager LogManager{ get; set; }
        public IMessageSerializationService _messageSerializationService { get; } = new MessageSerializationService();
        public IMonitoringService MonitoringService = NullMonitoringService.Instance;
        public INdmConsumerChannelManager? NdmConsumerChannelManager { get; set; }
        public INdmDataPublisher? NdmDataPublisher { get; set; }
        public INdmInitializer? NdmInitializer { get; set; }
        public INodeStatsManager? NodeStatsManager { get; set; }
        public IPeerManager? PeerManager { get; set; }
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        public IRlpxPeer? RlpxPeer;
        public IRpcModuleProvider? RpcModuleProvider { get; set; }
        public ISealer? Sealer { get; set; }
        public ISealValidator? SealValidator { get; set; }
        public ISigner? EngineSigner { get; set; }
        public ITxSigner? WalletSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public ISpecProvider? SpecProvider { get; set; }
        public ISyncModeSelector? SyncModeSelector { get; set; }
        public ISyncPeerPool? SyncPeerPool { get; set; }
        public ISynchronizer? Synchronizer { get; set; }
        public ISyncServer? SyncServer { get; set; }
        public IStateProvider? StateProvider { get; set; }
        public IStorageProvider? StorageProvider { get; set; }
        public ISessionMonitor? SessionMonitor { get; set; }
        public IStaticNodesManager? StaticNodesManager { get; set; }
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITransactionProcessor? TransactionProcessor { get; set; }
        public ITxSender? WalletTxSender { get; set; }
        public ITxPool? TxPool { get; set; }
        public ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        public IValidatorStore? ValidatorStore { get; set; }
        public IWallet? Wallet { get; set; }
        public IWebSocketsManager? WebSocketsManager;
        
        public List<IProducer> Producers { get; }= new List<IProducer>();
        public ProtectedPrivateKey? NodeKey { get; set; }
        public ProtectedPrivateKey? OriginalSignerKey { get; set; }
    }
}
