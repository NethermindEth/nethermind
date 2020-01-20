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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AuRa;
using Nethermind.AuRa.Config;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Core.Specs.GenesisFileStyle;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.Subprotocols.Serializers;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Facade.Config;
using Nethermind.Facade.Proxy;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Crypto;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.PubSub;
using Nethermind.PubSub.Kafka;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.BeamSyncStore;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;
using Nethermind.WebSockets;
using Block = Nethermind.Core.Block;
using ISyncConfig = Nethermind.Blockchain.ISyncConfig;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IRunner
    {
        private EthereumRunnerContext _context = new EthereumRunnerContext();

        public EthereumRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configurationProvider,
            ILogManager logManager, IGrpcServer grpcServer,
            INdmConsumerChannelManager ndmConsumerChannelManager, INdmDataPublisher ndmDataPublisher,
            INdmInitializer ndmInitializer, IWebSocketsManager webSocketsManager,
            IJsonSerializer ethereumJsonSerializer, IMonitoringService monitoringService)
        {
            _context.LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _context._grpcServer = grpcServer;
            _context._ndmConsumerChannelManager = ndmConsumerChannelManager;
            _context._ndmDataPublisher = ndmDataPublisher;
            _context._ndmInitializer = ndmInitializer;
            _context._webSocketsManager = webSocketsManager;
            _context._ethereumJsonSerializer = ethereumJsonSerializer;
            _context._monitoringService = monitoringService;
            _context.Logger = _context.LogManager.GetClassLogger();

            _context._configProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _context._rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            _context._initConfig = configurationProvider.GetConfig<IInitConfig>();
            _context._txPoolConfig = configurationProvider.GetConfig<ITxPoolConfig>();

            _context.NetworkConfig = _context._configProvider.GetConfig<INetworkConfig>();
            _context._ipResolver = new IpResolver(_context.NetworkConfig, _context.LogManager);
            _context.NetworkConfig.ExternalIp = _context._ipResolver.ExternalIp.ToString();
            _context.NetworkConfig.LocalIp = _context._ipResolver.LocalIp.ToString();
        }

        public async Task Start()
        {
            if (_context.Logger.IsDebug) _context.Logger.Debug("Initializing Ethereum");
            _context._runnerCancellation = new CancellationTokenSource();

            SetupKeyStore();
            LoadChainSpec();
            InitRlp();
            UpdateDiscoveryConfig();
            await InitBlockchain();

            if (_context.Logger.IsDebug) _context.Logger.Debug("Ethereum initialization completed");

            EthereumStepsManager stepsManager = new EthereumStepsManager(_context);
            stepsManager.DiscoverAll();
            await stepsManager.InitializeAll();
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        private void InitRlp()
        {
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(ParityTraceDecoder)));
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(NetworkNodeDecoder)));
            if (_context._chainSpec.SealEngineType == SealEngineType.AuRa)
            {
                Rlp.Decoders[typeof(BlockInfo)] = new BlockInfoDecoder(true);
            }
        }

        private void SetupKeyStore()
        {
            var encrypter = new AesEncrypter(
                _context._configProvider.GetConfig<IKeyStoreConfig>(),
                _context.LogManager);

            _context._keyStore = new FileKeyStore(
                _context._configProvider.GetConfig<IKeyStoreConfig>(),
                _context._ethereumJsonSerializer,
                encrypter,
                _context._cryptoRandom,
                _context.LogManager);

            _context._wallet = _context._initConfig switch
            {
                var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory
                  => new DevWallet(_context._configProvider.GetConfig<IWalletConfig>(), _context.LogManager),
                var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory
                  => new DevKeyStoreWallet(_context._keyStore, _context.LogManager),
                _ => NullWallet.Instance
            };

            INodeKeyManager nodeKeyManager = new NodeKeyManager(_context._cryptoRandom, _context._keyStore, _context._configProvider.GetConfig<IKeyStoreConfig>(), _context.LogManager);
            _context._nodeKey = nodeKeyManager.LoadNodeKey();
            _context._enode = new Enode(_context._nodeKey.PublicKey, IPAddress.Parse(_context.NetworkConfig.ExternalIp), _context.NetworkConfig.P2PPort);
        }

        private void UpdateDiscoveryConfig()
        {
            var discoveryConfig = _context._configProvider.GetConfig<IDiscoveryConfig>();
            if (discoveryConfig.Bootnodes != string.Empty)
            {
                if (_context._chainSpec.Bootnodes.Length != 0)
                {
                    discoveryConfig.Bootnodes += "," + string.Join(",", _context._chainSpec.Bootnodes.Select(bn => bn.ToString()));
                }
            }
            else
            {
                discoveryConfig.Bootnodes = string.Join(",", _context._chainSpec.Bootnodes.Select(bn => bn.ToString()));
            }
        }

        public async Task StopAsync()
        {
            if (_context.Logger.IsInfo) _context.Logger.Info("Shutting down...");
            _context._runnerCancellation.Cancel();

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping sesison monitor...");
            _context._sessionMonitor?.Stop();

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping discovery app...");
            var discoveryStopTask = _context._discoveryApp?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping block producer...");
            var blockProducerTask = _context._blockProducer?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping sync peer pool...");
            var peerPoolTask = _context._syncPeerPool?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping peer manager...");
            var peerManagerTask = _context.PeerManager?.StopAsync() ?? Task.CompletedTask;

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping synchronizer...");
            var synchronizerTask = (_context._synchronizer?.StopAsync() ?? Task.CompletedTask)
                .ContinueWith(t => _context._synchronizer?.Dispose());

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = (_context._blockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_context.Logger.IsInfo) _context.Logger.Info("Stopping rlpx peer...");
            var rlpxPeerTask = _context._rlpxPeer?.Shutdown() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

            if (_context.Logger.IsInfo) _context.Logger.Info("Closing DBs...");
            _context._dbProvider.Dispose();
            if (_context.Logger.IsInfo) _context.Logger.Info("All DBs closed.");

            while (_context._disposeStack.Count != 0)
            {
                var disposable = _context._disposeStack.Pop();
                if (_context.Logger.IsDebug) _context.Logger.Debug($"Disposing {disposable.GetType().Name}");
            }

            if (_context.Logger.IsInfo) _context.Logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void LoadChainSpec()
        {
            if (_context.Logger.IsInfo) _context.Logger.Info($"Loading chain spec from {_context._initConfig.ChainSpecPath}");

            IChainSpecLoader loader = string.Equals(_context._initConfig.ChainSpecFormat, "ChainSpec", StringComparison.InvariantCultureIgnoreCase)
                ? (IChainSpecLoader) new ChainSpecLoader(_context._ethereumJsonSerializer)
                : new GenesisFileLoader(_context._ethereumJsonSerializer);

            _context._chainSpec = loader.LoadFromFile(_context._initConfig.ChainSpecPath);
            _context._chainSpec.Bootnodes = _context._chainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_context._nodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private async Task InitBlockchain()
        {
            _context.SpecProvider = new ChainSpecBasedSpecProvider(_context._chainSpec);

            Account.AccountStartNonce = _context._chainSpec.Parameters.AccountStartNonce;

            /* sync */
            IDbConfig dbConfig = _context._configProvider.GetConfig<IDbConfig>();
            _context._syncConfig = _context._configProvider.GetConfig<ISyncConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            if (_context._syncConfig.BeamSyncEnabled)
            {
                _context._dbProvider = new BeamSyncDbProvider(_context._initConfig.BaseDbPath, dbConfig, _context.LogManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts || _context._syncConfig.DownloadReceiptsInFastSync);
            }
            else
            {
                _context._dbProvider = _context._initConfig.UseMemDb
                    ? (IDbProvider) new MemDbProvider()
                    : new RocksDbProvider(_context._initConfig.BaseDbPath, dbConfig, _context.LogManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts || _context._syncConfig.DownloadReceiptsInFastSync);
            }

            // IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts);
            // _context._dbProvider = new RpcDbProvider(_context._jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.Localhost, _context._jsonSerializer, _context._logManager), _context._logManager, debugRecorder);

            // IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_context._initConfig.BaseDbPath, "debug"), dbConfig, _context._logManager, _context._initConfig.StoreTraces, _context._initConfig.StoreReceipts), false);
            // _context._dbProvider = debugReader;

            _context._stateProvider = new StateProvider(
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context.LogManager);

            _context._ethereumEcdsa = new EthereumEcdsa(_context.SpecProvider, _context.LogManager);
            _context._txPool = new TxPool(
                new PersistentTxStorage(_context._dbProvider.PendingTxsDb, _context.SpecProvider),
                Timestamper.Default,
                _context._ethereumEcdsa,
                _context.SpecProvider,
                _context._txPoolConfig,
                _context._stateProvider,
                _context.LogManager);

            _context._receiptStorage = new PersistentReceiptStorage(_context._dbProvider.ReceiptsDb, _context.SpecProvider, _context.LogManager);

            _context._chainLevelInfoRepository = new ChainLevelInfoRepository(_context._dbProvider.BlockInfosDb);

            _context.BlockTree = new BlockTree(
                _context._dbProvider.BlocksDb,
                _context._dbProvider.HeadersDb,
                _context._dbProvider.BlockInfosDb,
                _context._chainLevelInfoRepository,
                _context.SpecProvider,
                _context._txPool,
                _context._syncConfig,
                _context.LogManager);

            // Init state if we need system calls before actual processing starts
            if (_context.BlockTree.Head != null)
            {
                _context._stateProvider.StateRoot = _context.BlockTree.Head.StateRoot;
            }

            _context._recoveryStep = new TxSignaturesRecoveryStep(_context._ethereumEcdsa, _context._txPool, _context.LogManager);

            _context._snapshotManager = null;


            _context._storageProvider = new StorageProvider(
                _context._dbProvider.StateDb,
                _context._stateProvider,
                _context.LogManager);

            IList<IAdditionalBlockProcessor> additionalBlockProcessors = new List<IAdditionalBlockProcessor>();
            // blockchain processing
            var blockhashProvider = new BlockhashProvider(
                _context.BlockTree, _context.LogManager);

            var virtualMachine = new VirtualMachine(
                _context._stateProvider,
                _context._storageProvider,
                blockhashProvider,
                _context.SpecProvider,
                _context.LogManager);

            _context._transactionProcessor = new TransactionProcessor(
                _context.SpecProvider,
                _context._stateProvider,
                _context._storageProvider,
                virtualMachine,
                _context.LogManager);

            InitSealEngine(additionalBlockProcessors);

            /* validation */
            _context._headerValidator = new HeaderValidator(
                _context.BlockTree,
                _context._sealValidator,
                _context.SpecProvider,
                _context.LogManager);

            var ommersValidator = new OmmersValidator(
                _context.BlockTree,
                _context._headerValidator,
                _context.LogManager);

            var txValidator = new TxValidator(_context.SpecProvider.ChainId);

            _context._blockValidator = new BlockValidator(
                txValidator,
                _context._headerValidator,
                ommersValidator,
                _context.SpecProvider,
                _context.LogManager);

            _context._txPoolInfoProvider = new TxPoolInfoProvider(_context._stateProvider, _context._txPool);

            _context._blockProcessor = new BlockProcessor(
                _context.SpecProvider,
                _context._blockValidator,
                _context._rewardCalculator,
                _context._transactionProcessor,
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context._dbProvider.TraceDb,
                _context._stateProvider,
                _context._storageProvider,
                _context._txPool,
                _context._receiptStorage,
                _context.LogManager,
                additionalBlockProcessors);

            _context._blockchainProcessor = new BlockchainProcessor(
                _context.BlockTree,
                _context._blockProcessor,
                _context._recoveryStep,
                _context.LogManager,
                _context._initConfig.StoreReceipts,
                _context._initConfig.StoreTraces);

            _context._finalizationManager = InitFinalizationManager(additionalBlockProcessors);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _context._configProvider.GetConfig<IStatsConfig>();
            _context._nodeStatsManager = new NodeStatsManager(statsConfig, _context.LogManager);

            _context._blockchainProcessor.Start();
            LoadGenesisBlock(string.IsNullOrWhiteSpace(_context._initConfig.GenesisHash) ? null : new Keccak(_context._initConfig.GenesisHash));

            InitBlockProducers();

            if (_context._initConfig.ProcessingEnabled)
            {
#pragma warning disable 4014
                RunBlockTreeInitTasks();
#pragma warning restore 4014
            }
            else
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await _context._blockchainProcessor.StopAsync();
            }

            ISubscription subscription;
            if (_context.Producers.Any())
            {
                subscription = new Subscription(_context.Producers, _context._blockProcessor, _context.LogManager);
            }
            else
            {
                subscription = new EmptySubscription();
            }

            _context._disposeStack.Push(subscription);

            await InitializeNetwork();
        }

        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context._chainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    return new AuRaBlockFinalizationManager(_context.BlockTree, _context._chainLevelInfoRepository, _context._blockProcessor,
                        blockPreProcessors.OfType<IAuRaValidator>().First(), _context.LogManager);
                default:
                    return null;
            }
        }

        private void InitBlockProducers()
        {
            ReadOnlyChain GetProducerChain(
                Func<IDb, IStateProvider, IBlockTree, ITransactionProcessor, ILogManager, IEnumerable<IAdditionalBlockProcessor>> createAdditionalBlockProcessors = null,
                bool allowStateModification = false)
            {
                IReadOnlyDbProvider minerDbProvider = new ReadOnlyDbProvider(_context._dbProvider, allowStateModification);
                ReadOnlyBlockTree readOnlyBlockTree = new ReadOnlyBlockTree(_context.BlockTree);
                ReadOnlyChain producerChain = new ReadOnlyChain(readOnlyBlockTree, _context._blockValidator, _context._rewardCalculator,
                    _context.SpecProvider, minerDbProvider, _context._recoveryStep, _context.LogManager, _context._txPool, _context._receiptStorage,
                    createAdditionalBlockProcessors);
                return producerChain;
            }

            if (_context._initConfig.IsMining)
            {
                switch (_context._chainSpec.SealEngineType)
                {
                    case SealEngineType.Clique:
                    {
                        ReadOnlyChain producerChain = GetProducerChain();
                        PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Clique block producer & sealer");
                        CliqueConfig cliqueConfig = new CliqueConfig();
                        cliqueConfig.BlockPeriod = _context._chainSpec.Clique.Period;
                        cliqueConfig.Epoch = _context._chainSpec.Clique.Epoch;
                        _context._blockProducer = new CliqueBlockProducer(pendingTransactionSelector, producerChain.Processor,
                            _context.BlockTree, _context._timestamper, _context._cryptoRandom, producerChain.ReadOnlyStateProvider, _context._snapshotManager, (CliqueSealer) _context._sealer, _context._nodeKey.Address, cliqueConfig, _context.LogManager);
                        break;
                    }

                    case SealEngineType.NethDev:
                    {
                        ReadOnlyChain producerChain = GetProducerChain();
                        PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Dev block producer & sealer");
                        _context._blockProducer = new DevBlockProducer(pendingTransactionSelector, producerChain.Processor, _context.BlockTree, producerChain.ReadOnlyStateProvider, _context._timestamper, _context.LogManager, _context._txPool);
                        break;
                    }

                    case SealEngineType.AuRa:
                    {
                        IAuRaValidatorProcessor validator = null;
                        ReadOnlyChain producerChain = GetProducerChain((db, s, b, t, l) => new[] {validator = new AuRaAdditionalBlockProcessorFactory(db, s, new AbiEncoder(), t, b, _context._receiptStorage, l).CreateValidatorProcessor(_context._chainSpec.AuRa.Validators)});
                        PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Starting AuRa block producer & sealer");
                        _context._blockProducer = new AuRaBlockProducer(pendingTransactionSelector, producerChain.Processor, _context._sealer, _context.BlockTree, producerChain.ReadOnlyStateProvider, _context._timestamper, _context.LogManager, new AuRaStepCalculator(_context._chainSpec.AuRa.StepDuration, _context._timestamper), _context._configProvider.GetConfig<IAuraConfig>(), _context._nodeKey.Address);
                        validator.SetFinalizationManager(_context._finalizationManager, true);
                        break;
                    }

                    default:
                        throw new NotSupportedException($"Mining in {_context._chainSpec.SealEngineType} mode is not supported");
                }

                _context._blockProducer.Start();
            }
        }

        private void InitSealEngine(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_context._chainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _context._sealer = NullSealEngine.Instance;
                    _context._sealValidator = NullSealEngine.Instance;
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _context._chainSpec.Clique.Period;
                    cliqueConfig.Epoch = _context._chainSpec.Clique.Epoch;
                    _context._snapshotManager = new SnapshotManager(cliqueConfig, _context._dbProvider.BlocksDb, _context.BlockTree, _context._ethereumEcdsa, _context.LogManager);
                    _context._sealValidator = new CliqueSealValidator(cliqueConfig, _context._snapshotManager, _context.LogManager);
                    _context._recoveryStep = new CompositeDataRecoveryStep(_context._recoveryStep, new AuthorRecoveryStep(_context._snapshotManager));
                    if (_context._initConfig.IsMining)
                    {
                        _context._sealer = new CliqueSealer(new BasicWallet(_context._nodeKey), cliqueConfig, _context._snapshotManager, _context._nodeKey.Address, _context.LogManager);
                    }
                    else
                    {
                        _context._sealer = NullSealEngine.Instance;
                    }

                    break;
                case SealEngineType.NethDev:
                    _context._sealer = NullSealEngine.Instance;
                    _context._sealValidator = NullSealEngine.Instance;
                    _context._rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Ethash:
                    _context._rewardCalculator = new RewardCalculator(_context.SpecProvider);
                    var difficultyCalculator = new DifficultyCalculator(_context.SpecProvider);
                    if (_context._initConfig.IsMining)
                    {
                        _context._sealer = new EthashSealer(new Ethash(_context.LogManager), _context.LogManager);
                    }
                    else
                    {
                        _context._sealer = NullSealEngine.Instance;
                    }

                    _context._sealValidator = new EthashSealValidator(_context.LogManager, difficultyCalculator, _context._cryptoRandom, new Ethash(_context.LogManager));
                    break;
                case SealEngineType.AuRa:
                    var abiEncoder = new AbiEncoder();
                    var validatorProcessor = new AuRaAdditionalBlockProcessorFactory(_context._dbProvider.StateDb, _context._stateProvider, abiEncoder, _context._transactionProcessor, _context.BlockTree, _context._receiptStorage, _context.LogManager)
                        .CreateValidatorProcessor(_context._chainSpec.AuRa.Validators);

                    var auRaStepCalculator = new AuRaStepCalculator(_context._chainSpec.AuRa.StepDuration, _context._timestamper);
                    _context._sealValidator = new AuRaSealValidator(_context._chainSpec.AuRa, auRaStepCalculator, validatorProcessor, _context._ethereumEcdsa, _context.LogManager);
                    _context._rewardCalculator = new AuRaRewardCalculator(_context._chainSpec.AuRa, abiEncoder, _context._transactionProcessor);
                    _context._sealer = new AuRaSealer(_context.BlockTree, validatorProcessor, auRaStepCalculator, _context._nodeKey.Address, new BasicWallet(_context._nodeKey), _context.LogManager);
                    blockPreProcessors.Add(validatorProcessor);
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_context._chainSpec.SealEngineType} is not supported in Nethermind");
            }
        }

        private async Task RunBlockTreeInitTasks()
        {
            if (!_context._syncConfig.SynchronizationEnabled)
            {
                return;
            }

            if (!_context._syncConfig.FastSync)
            {
                await _context.BlockTree.LoadBlocksFromDb(_context._runnerCancellation.Token, null).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_context.Logger.IsError) _context.Logger.Error("Loading blocks from the DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Loading blocks from the DB canceled.");
                    }
                });
            }
            else
            {
                await _context.BlockTree.FixFastSyncGaps(_context._runnerCancellation.Token).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_context.Logger.IsError) _context.Logger.Error("Fixing gaps in DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_context.Logger.IsWarn) _context.Logger.Warn("Fixing gaps in DB canceled.");
                    }
                });
            }
        }

        private async Task InitializeNetwork()
        {
            if (_context.NetworkConfig.DiagTracerEnabled)
            {
                NetworkDiagTracer.IsEnabled = true;
                NetworkDiagTracer.Start();
            }

            var maxPeersCount = _context.NetworkConfig.ActivePeersMaxCount;
            _context._syncPeerPool = new EthSyncPeerPool(_context.BlockTree, _context._nodeStatsManager, _context._syncConfig, maxPeersCount, _context.LogManager);
            NodeDataFeed feed = new NodeDataFeed(_context._dbProvider.CodeDb, _context._dbProvider.StateDb, _context.LogManager);
            NodeDataDownloader nodeDataDownloader = new NodeDataDownloader(_context._syncPeerPool, feed, NullDataConsumer.Instance, _context.LogManager);
            _context._synchronizer = new Synchronizer(_context.SpecProvider, _context.BlockTree, _context._receiptStorage, _context._blockValidator, _context._sealValidator, _context._syncPeerPool, _context._syncConfig, nodeDataDownloader, _context._nodeStatsManager, _context.LogManager);

            _context._syncServer = new SyncServer(
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context.BlockTree,
                _context._receiptStorage,
                _context._blockValidator,
                _context._sealValidator,
                _context._syncPeerPool,
                _context._synchronizer,
                _context._syncConfig,
                _context.LogManager);

            InitDiscovery();
            await InitPeer().ContinueWith(initPeerTask =>
            {
                if (initPeerTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to init the peer manager.", initPeerTask.Exception);
                }
            });

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to start the synchronizer.", initNetTask.Exception);
                }
            });

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to start the discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            try
            {
                StartPeer();
            }
            catch (Exception e)
            {
                _context.Logger.Error("Unable to start the peer manager.", e);
            }

            if (_context.Logger.IsInfo) _context.Logger.Info($"Ethereum     : tcp://{_context._enode.HostIp}:{_context._enode.Port}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"Version      : {ClientVersion.Description}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"This node    : {_context._enode.Info}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"Node address : {_context._enode.Address} (do not use as an account)");
        }

        private void LoadGenesisBlock(Keccak expectedGenesisHash)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_context.BlockTree.Genesis != null)
            {
                ValidateGenesisHash(expectedGenesisHash);
                return;
            }

            Block genesis = _context._chainSpec.Genesis;
            CreateSystemAccounts();

            foreach ((Address address, ChainSpecAllocation allocation) in _context._chainSpec.Allocations)
            {
                _context._stateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = _context._stateProvider.UpdateCode(allocation.Code);
                    _context._stateProvider.UpdateCodeHash(address, codeHash, _context.SpecProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };
                    _context._transactionProcessor.Execute(constructorTransaction, genesis.Header, NullTxTracer.Instance);
                }
            }

            _context._storageProvider.Commit();
            _context._stateProvider.Commit(_context.SpecProvider.GenesisSpec);

            _context._storageProvider.CommitTrees();
            _context._stateProvider.CommitTree();

            _context._dbProvider.StateDb.Commit();
            _context._dbProvider.CodeDb.Commit();

            genesis.StateRoot = _context._stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                _context.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            _context.BlockTree.NewHeadBlock += GenesisProcessed;
            _context.BlockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(40));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }

            ValidateGenesisHash(expectedGenesisHash);
        }

        private void CreateSystemAccounts()
        {
            var isAura = _context._chainSpec.SealEngineType == SealEngineType.AuRa;
            var hasConstructorAllocation = _context._chainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (isAura && hasConstructorAllocation)
            {
                _context._stateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _context._storageProvider.Commit();
                _context._stateProvider.Commit(Homestead.Instance);
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak expectedGenesisHash)
        {
            if (expectedGenesisHash != null && _context.BlockTree.Genesis.Hash != expectedGenesisHash)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context._stateProvider.DumpState());
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_context.Logger.IsError) _context.Logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_context.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_context.Logger.IsInfo) _context.Logger.Info($"Genesis hash :  {_context.BlockTree.Genesis.Hash}");
            }
        }

        private Task StartSync()
        {
            if (!_context._syncConfig.SynchronizationEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping blockchain synchronization init due to ({nameof(ISyncConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug($"Starting synchronization from block {_context.BlockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _context._syncPeerPool.Start();
            _context._synchronizer.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            var eciesCipher = new EciesCipher(_context._cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_context._cryptoRandom);
            _context._messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _context._messageSerializationService.Register(new ReceiptsMessageSerializer(_context.SpecProvider));

            var encryptionHandshakeServiceA = new HandshakeService(_context._messageSerializationService, eciesCipher,
                _context._cryptoRandom, new Ecdsa(), _context._nodeKey, _context.LogManager);

            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            var discoveryConfig = _context._configProvider.GetConfig<IDiscoveryConfig>();

            _context._sessionMonitor = new SessionMonitor(_context.NetworkConfig, _context.LogManager);
            _context._rlpxPeer = new RlpxPeer(
                _context._messageSerializationService,
                _context._nodeKey.PublicKey,
                _context.NetworkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _context.LogManager,
                _context._sessionMonitor);

            await _context._rlpxPeer.Init();

            _context._staticNodesManager = new StaticNodesManager(_context._initConfig.StaticNodesPath, _context.LogManager);
            await _context._staticNodesManager.InitAsync();

            var peersDb = new SimpleFilePublicKeyDb("PeersDB", _context.PeersDbPath.GetApplicationResourcePath(_context._initConfig.BaseDbPath), _context.LogManager);
            var peerStorage = new NetworkStorage(peersDb, _context.LogManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_context._nodeStatsManager, _context.BlockTree, _context.LogManager);
            _context._protocolsManager = new ProtocolsManager(_context._syncPeerPool, _context._syncServer, _context._txPool, _context._discoveryApp, _context._messageSerializationService, _context._rlpxPeer, _context._nodeStatsManager, protocolValidator, peerStorage, _context.LogManager);

            if (!(_context._ndmInitializer is null))
            {
                if (_context.Logger.IsInfo) _context.Logger.Info($"Initializing NDM...");
                _context._httpClient = new DefaultHttpClient(new HttpClient(), _context._ethereumJsonSerializer, _context.LogManager);
                var ndmConfig = _context._configProvider.GetConfig<INdmConfig>();
                if (ndmConfig.ProxyEnabled)
                {
                    _context._jsonRpcClientProxy = new JsonRpcClientProxy(_context._httpClient, ndmConfig.JsonRpcUrlProxies,
                        _context.LogManager);
                    _context._ethJsonRpcClientProxy = new EthJsonRpcClientProxy(_context._jsonRpcClientProxy);
                }

                var filterStore = new FilterStore();
                var filterManager = new FilterManager(filterStore, _context._blockProcessor, _context._txPool, _context.LogManager);
                var capabilityConnector = await _context._ndmInitializer.InitAsync(_context._configProvider, _context._dbProvider,
                    _context._initConfig.BaseDbPath, _context.BlockTree, _context._txPool, _context.SpecProvider, _context._receiptStorage, _context._wallet, filterStore,
                    filterManager, _context._timestamper, _context._ethereumEcdsa, _context._rpcModuleProvider, _context._keyStore, _context._ethereumJsonSerializer,
                    _context._cryptoRandom, _context._enode, _context._ndmConsumerChannelManager, _context._ndmDataPublisher, _context._grpcServer,
                    _context._nodeStatsManager, _context._protocolsManager, protocolValidator, _context._messageSerializationService,
                    _context._initConfig.EnableUnsecuredDevWallet, _context._webSocketsManager, _context.LogManager, _context._blockProcessor,
                    _context._jsonRpcClientProxy, _context._ethJsonRpcClientProxy, _context._httpClient, _context._monitoringService);
                capabilityConnector.Init();
                if (_context.Logger.IsInfo) _context.Logger.Info($"NDM initialized.");
            }

            PeerLoader peerLoader = new PeerLoader(_context.NetworkConfig, discoveryConfig, _context._nodeStatsManager, peerStorage, _context.LogManager);
            _context.PeerManager = new PeerManager(_context._rlpxPeer, _context._discoveryApp, _context._nodeStatsManager, peerStorage, peerLoader, _context.NetworkConfig, _context.LogManager, _context._staticNodesManager);
            _context.PeerManager.Init();
        }

        private void StartPeer()
        {
            if (!_context._initConfig.PeerManagerEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping peer manager init due to {nameof(_context._initConfig.PeerManagerEnabled)} set to false)");
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Initializing peer manager");
            _context.PeerManager.Start();
            _context._sessionMonitor.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (!_context._initConfig.DiscoveryEnabled)
            {
                _context._discoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _context._configProvider.GetConfig<IDiscoveryConfig>();

            var privateKeyProvider = new SameKeyGenerator(_context._nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_context._timestamper);
            var nodeIdResolver = new NodeIdResolver(_context._ethereumEcdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _context._messageSerializationService,
                _context._ethereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            var nodeTable = new NodeTable(nodeDistanceCalculator, discoveryConfig, _context.NetworkConfig, _context.LogManager);
            var evictionManager = new EvictionManager(nodeTable, _context.LogManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _context._nodeStatsManager,
                discoveryConfig,
                _context.LogManager);

            var discoveryDb = new SimpleFilePublicKeyDb("DiscoveryDB", _context.DiscoveryNodesDbPath.GetApplicationResourcePath(_context._initConfig.BaseDbPath), _context.LogManager);
            var discoveryStorage = new NetworkStorage(
                discoveryDb,
                _context.LogManager);

            var discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _context.LogManager);

            var nodesLocator = new NodesLocator(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _context.LogManager);

            _context._discoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _context._messageSerializationService,
                _context._cryptoRandom,
                discoveryStorage,
                _context.NetworkConfig,
                discoveryConfig,
                _context._timestamper,
                _context.LogManager);

            _context._discoveryApp.Initialize(_context._nodeKey.PublicKey);
        }

        private Task StartDiscovery()
        {
            if (!_context._initConfig.DiscoveryEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Starting discovery process.");
            _context._discoveryApp.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }
    }
}