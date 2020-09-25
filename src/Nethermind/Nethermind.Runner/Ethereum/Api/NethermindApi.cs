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
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.PubSub;
using Nethermind.Runner.Extensions;
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
        public NethermindApi(IConfigProvider configProvider, ILogManager logManager)
        {
            ConfigProvider = configProvider;
            LogManager = logManager;
            
            CryptoRandom = new CryptoRandom();
            DisposeStack.Push(CryptoRandom);
        }
        
        public IAbiEncoder AbiEncoder { get; } = new AbiEncoder();
        public ChainSpec? ChainSpec { get; set; }
        public DisposableStack DisposeStack { get; } = new DisposableStack();

        public IBlockchainBridge CreateBlockchainBridge()
        {
            ReadOnlyBlockTree readOnlyTree = new ReadOnlyBlockTree(BlockTree);
            IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(DbProvider, false);
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, SpecProvider, LogManager);

            IMiningConfig miningConfig = ConfigProvider.GetConfig<IMiningConfig>();
            ISyncConfig syncConfig = ConfigProvider.GetConfig<ISyncConfig>();
            IJsonRpcConfig rpcConfig = ConfigProvider.GetConfig<IJsonRpcConfig>();
            return new BlockchainBridge(
                readOnlyTxProcessingEnv,
                TxPool,
                ReceiptFinder,
                FilterStore,
                FilterManager,
                EthereumEcdsa,
                BloomStorage,
                Timestamper,
                LogManager,
                miningConfig.Enabled,
                syncConfig.BeamSync && syncConfig.FastSync,
                rpcConfig.FindLogBlockDepthLimit
            );
        }

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
        public IEthJsonRpcClientProxy? EthJsonRpcClientProxy { get; set; }
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IFilterStore FilterStore { get; set; }
        public IFilterManager FilterManager { get; set; }
        public IGrpcServer? GrpcServer { get; set; }
        public IHeaderValidator? HeaderValidator { get; set; }
        public IIPResolver? IpResolver { get; set; }
        public IJsonRpcClientProxy? JsonRpcClientProxy { get; set; }
        public IJsonSerializer? EthereumJsonSerializer { get; set; }
        public IKeyStore? KeyStore { get; set; }
        public ILogManager LogManager { get; }
        public IMessageSerializationService MessageSerializationService { get; } = new MessageSerializationService();
        public IMonitoringService MonitoringService { get; set; } = NullMonitoringService.Instance;
        public INodeStatsManager? NodeStatsManager { get; set; }
        public IPeerManager? PeerManager { get; set; }
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IProtocolValidator? ProtocolValidator { get; set; }
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        public IRlpxPeer? RlpxPeer { get; set; }
        public IRpcModuleProvider? RpcModuleProvider { get; set; }
        public ISealer? Sealer { get; set; }
        public ISealValidator? SealValidator { get; set; }
        public ISigner? EngineSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public ISpecProvider? SpecProvider { get; set; }
        public ISyncModeSelector? SyncModeSelector { get; set; }
        public ISyncPeerPool? SyncPeerPool { get; set; }
        public ISynchronizer? Synchronizer { get; set; }
        public ISyncServer? SyncServer { get; set; }
        public IStateProvider? StateProvider { get; set; }
        public IStateReader? StateReader { get; set; }
        public IStorageProvider? StorageProvider { get; set; }
        public ISessionMonitor? SessionMonitor { get; set; }
        public IStaticNodesManager? StaticNodesManager { get; set; }
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITransactionProcessor? TransactionProcessor { get; set; }
        public ITxSender? TxSender { get; set; }
        public ITxPool? TxPool { get; set; }
        public ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        public IValidatorStore? ValidatorStore { get; set; }
        public IWallet? Wallet { get; set; }
        public IWebSocketsManager? WebSocketsManager { get; set; }
        
        public List<IProducer> Producers { get; }= new List<IProducer>();
        public ProtectedPrivateKey? NodeKey { get; set; }
        public ProtectedPrivateKey? OriginalSignerKey { get; set; }

        public List<IPlugin> Plugins { get; }
    }
}
