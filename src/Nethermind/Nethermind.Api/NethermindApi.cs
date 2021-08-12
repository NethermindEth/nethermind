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

using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.PubSub;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
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

namespace Nethermind.Api
{
    public class NethermindApi : INethermindApi
    {
        public NethermindApi()
        {
            CryptoRandom = new CryptoRandom();
            DisposeStack.Push(CryptoRandom);
        }

        private IReadOnlyDbProvider? _readOnlyDbProvider;
        
        public IBlockchainBridge CreateBlockchainBridge()
        {
            ReadOnlyBlockTree readOnlyTree = BlockTree.AsReadOnly();
            LazyInitializer.EnsureInitialized(ref _readOnlyDbProvider, () => new ReadOnlyDbProvider(DbProvider, false));

            // TODO: reuse the same trie cache here
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new(
                _readOnlyDbProvider,
                ReadOnlyTrieStore,
                readOnlyTree,
                SpecProvider,
                LogManager);

            IMiningConfig miningConfig = ConfigProvider.GetConfig<IMiningConfig>();
            ISyncConfig syncConfig = ConfigProvider.GetConfig<ISyncConfig>();

            return new BlockchainBridge(
                readOnlyTxProcessingEnv,
                TxPool,
                ReceiptFinder,
                FilterStore,
                FilterManager,
                EthereumEcdsa,
                Timestamper,
                LogFinder,
                SpecProvider!,
                miningConfig.Enabled,
                syncConfig.BeamSync && syncConfig.FastSync
            );
        }

        public IAbiEncoder AbiEncoder { get; } = Nethermind.Abi.AbiEncoder.Instance;
        public IBlockchainProcessor? BlockchainProcessor { get; set; }
        public CompositeBlockPreprocessorStep BlockPreprocessor { get; } = new();
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
        public IRocksDbFactory? RocksDbFactory { get; set; }
        public IMemDbFactory? MemDbFactory { get; set; }
        public IDisconnectsAnalyzer? DisconnectsAnalyzer { get; set; }
        public IDiscoveryApp? DiscoveryApp { get; set; }
        public ISigner? EngineSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public IEnode? Enode { get; set; }
        public IEthereumEcdsa? EthereumEcdsa { get; set; }
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IFilterStore? FilterStore { get; set; }
        public IFilterManager? FilterManager { get; set; }
        public IGrpcServer? GrpcServer { get; set; }
        public IHeaderValidator? HeaderValidator { get; set; }
        public IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; } =
            new BuildBlocksWhenRequested();
        public IIPResolver? IpResolver { get; set; }
        public IJsonSerializer EthereumJsonSerializer { get; set; }
        public IKeyStore? KeyStore { get; set; }
        public IPasswordProvider? PasswordProvider { get; set; }
        public ILogFinder? LogFinder { get; set; }
        public ILogManager LogManager { get; set; }
        public IKeyValueStoreWithBatching? MainStateDbWithCache { get; set; }
        public IMessageSerializationService MessageSerializationService { get; } = new MessageSerializationService();
        public IMonitoringService MonitoringService { get; set; } = NullMonitoringService.Instance;
        public INodeStatsManager? NodeStatsManager { get; set; }
        public IPeerManager? PeerManager { get; set; }
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IProtocolValidator? ProtocolValidator { get; set; }
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IWitnessCollector? WitnessCollector { get; set; }
        public IWitnessRepository? WitnessRepository { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; } = NoBlockRewards.Instance;
        public IRlpxPeer? RlpxPeer { get; set; }
        public IRpcModuleProvider RpcModuleProvider { get; set; } = NullModuleProvider.Instance;
        public ISealer? Sealer { get; set; } = NullSealEngine.Instance;
        public string SealEngineType { get; set; } = Nethermind.Core.SealEngineType.None;
        public ISealValidator? SealValidator { get; set; } = NullSealEngine.Instance;
        public ISessionMonitor? SessionMonitor { get; set; }
        public ISpecProvider? SpecProvider { get; set; }
        public ISyncModeSelector? SyncModeSelector { get; set; }
        public ISyncPeerPool? SyncPeerPool { get; set; }
        public ISynchronizer? Synchronizer { get; set; }
        public ISyncServer? SyncServer { get; set; }
        public IStateProvider? StateProvider { get; set; }
        public IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        public IStateReader? StateReader { get; set; }
        public IStorageProvider? StorageProvider { get; set; }
        public IStaticNodesManager? StaticNodesManager { get; set; }
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITimerFactory TimerFactory { get; } = Core.Timers.TimerFactory.Default;
        public ITransactionProcessor? TransactionProcessor { get; set; }
        public ITrieStore? TrieStore { get; set; }
        public IReadOnlyTrieStore? ReadOnlyTrieStore { get; set; }
        public ITxSender? TxSender { get; set; }
        public ITxPool? TxPool { get; set; }
        public ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        public IHealthHintService? HealthHintService { get; set; }
        public TxValidator? TxValidator { get; set; }
        public IBlockFinalizationManager? FinalizationManager { get; set; }
        public IGasLimitCalculator GasLimitCalculator { get; set; }
        
        public IBlockProducerEnvFactory BlockProducerEnvFactory { get; set; }
        public IWallet? Wallet { get; set; }
        public ITransactionComparerProvider TransactionComparerProvider { get; set; }
        public IWebSocketsManager WebSocketsManager { get; set; } = new WebSocketsManager();

        public ProtectedPrivateKey? NodeKey { get; set; }
        
        /// <summary>
        /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>. 
        /// </summary>
        public ProtectedPrivateKey? OriginalSignerKey { get; set; }

        public ChainSpec? ChainSpec { get; set; }
        public DisposableStack DisposeStack { get; } = new();
        public IReadOnlyList<INethermindPlugin> Plugins { get; } = new List<INethermindPlugin>();
        public IList<IPublisher> Publishers { get; } = new List<IPublisher>(); // this should be called publishers
    }
}
