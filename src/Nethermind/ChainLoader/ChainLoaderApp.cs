/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Json;
using Nethermind.Logging;
using Nethermind.Core.Specs;

using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;

using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;

namespace ChainLoader
{
    public class ChainLoaderApp : RunnerAppBase, IRunnerApp
    {
        private readonly string _defaultConfigFile = Path.Combine("configs", "mainnet.cfg");
       
        private static ILogManager _logManager;
        private static ILogger _logger;
        private IConfigProvider _configProvider;
        private IInitConfig _initConfig;
        private ITxPoolConfig _txPoolConfig;
        private IHeaderValidator _headerValidator;
        private ISpecProvider _specProvider;
        private IReceiptStorage _receiptStorage;
        private IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        private IJsonSerializer _ethereumJsonSerializer = new EthereumJsonSerializer();
        private IBlockValidator _blockValidator;
        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private IBlockchainProcessor _blockchainProcessor;
        private IWallet _wallet;
        private INodeStatsManager _nodeStatsManager;
        private IPerfService _perfService;
        private ISealer _sealer;
        private IBlockTree _blockTree;
        private ISnapshotManager _snapshotManager;
        private IBlockDataRecoveryStep _recoveryStep;
        private IBlockProcessor _blockProcessor;
        private IRewardCalculator _rewardCalculator;
        private IEthereumEcdsa _ethereumEcdsa;
        private ITxPool _txPool;
        private IBlockProducer _blockProducer;
        private ChainSpec _chainSpec;
        private IDbProvider _dbProvider;
        private readonly ITimestamp _timestamp = new Timestamp();
        private IStateProvider _stateProvider;
        private IKeyStore _keyStore;
        private PrivateKey _nodeKey;
        private IEnode _enode;
        private INetworkHelper _networkHelper;
        private ISealValidator _sealValidator;
        private IConfigProvider configProvider;
        private ILogManager logManager;
   

        public ChainLoaderApp(ILogger logger) : base(logger)
        {
        }
        
        private static List<Type> _configs = new List<Type>
        {
            typeof(KeyStoreConfig),
            typeof(NetworkConfig),
            typeof(JsonRpcConfig),
            typeof(InitConfig),
            typeof(DbConfig),
            typeof(StatsConfig),
            typeof(SyncConfig)
        };
        private void InitRlp()
        {
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(ParityTraceDecoder)));
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(NetworkNodeDecoder)));
        }
        private void LoadChainSpec()
        {
            _logger.Info($"Loading chain spec from {_initConfig.ChainSpecPath}");

            IChainSpecLoader loader = string.Equals(_initConfig.ChainSpecFormat, "ChainSpec", StringComparison.InvariantCultureIgnoreCase)
                ? (IChainSpecLoader)new ChainSpecLoader(_ethereumJsonSerializer)
                : new GenesisFileLoader(_ethereumJsonSerializer);

            _chainSpec = loader.LoadFromFile(_initConfig.ChainSpecPath);
            _chainSpec.Bootnodes = _chainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_nodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
        }
        private void SetupKeyStore()
        {
            var encrypter = new AesEncrypter(
                _configProvider.GetConfig<IKeyStoreConfig>(),
                _logManager);

            _keyStore = new FileKeyStore(
                _configProvider.GetConfig<IKeyStoreConfig>(),
                _ethereumJsonSerializer,
                encrypter,
                _cryptoRandom,
                _logManager);

            switch (_initConfig)
            {
  
                case var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory:
                    _wallet = new DevWallet(_logManager);
                    break;
                case var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory:
                    _wallet = new DevKeyStoreWallet(_keyStore, _logManager);
                    break;
                default:
                    _wallet = new NullWallet();
                    break;
            }

            INodeKeyManager nodeKeyManager = new NodeKeyManager(_cryptoRandom, _keyStore, _configProvider.GetConfig<IKeyStoreConfig>(), _logManager);
            _nodeKey = nodeKeyManager.LoadNodeKey();

            var ipVariable = Environment.GetEnvironmentVariable("NETHERMIND_ENODE_IPADDRESS");
            var localIp = string.IsNullOrWhiteSpace(ipVariable)
                ? _networkHelper.GetLocalIp()
                : IPAddress.Parse(ipVariable);

            _enode = new Enode(_nodeKey.PublicKey, localIp, _initConfig.P2PPort);
        }

        private TaskCompletionSource<object> _cancelKeySource;
        private void LogMemoryConfiguration()
        {
            if (Logger.IsDebug) Logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
            if (Logger.IsDebug) Logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
            if (Logger.IsDebug)
                Logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
        }


        new public void Run(string[] args)
        {
            var (app, buildConfigProvider, getDbBasePath) = BuildCommandLineApp();
            ManualResetEventSlim appClosed = new ManualResetEventSlim(false);
            app.OnExecute(async () =>
            {
                var configProvider = buildConfigProvider();
                var initConfig = configProvider.GetConfig<IInitConfig>();
                
                Logger = new NLogLogger(initConfig.LogFileName, initConfig.LogDirectory);
                LogMemoryConfiguration();

                var pathDbPath = getDbBasePath();
                if (!string.IsNullOrWhiteSpace(pathDbPath))
                {
                    var newDbPath = Path.Combine(pathDbPath, initConfig.BaseDbPath);
                    if (Logger.IsDebug) Logger.Debug($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
                    initConfig.BaseDbPath = newDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");
                }

                Console.Title = initConfig.LogFileName;
                

                var serializer = new UnforgivingJsonSerializer();
                if (Logger.IsInfo)
                    Logger.Info($"Nethermind config:\n{serializer.Serialize(initConfig, true)}\n");

               

                await StartRunners(configProvider);
            
                Console.WriteLine("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            app.Execute(args);
            appClosed.Wait();
        }


        [Todo(Improve.Refactor, "network config can be handled internally in EthereumRunner")]
        new protected  async Task StartRunners(IConfigProvider configProvider)
        {
            InitRlp();
            _configProvider = configProvider;
             _initConfig = _configProvider.GetConfig<IInitConfig>();
             _initConfig = _configProvider.GetConfig<IInitConfig>();
            _logManager = new NLogManager(_initConfig.LogFileName, _initConfig.LogDirectory);
            _logger = _logManager.GetClassLogger();
            _networkHelper = new NetworkHelper(_logger);
            SetupKeyStore();
            LoadChainSpec();
            IDbConfig dbConfig = _configProvider.GetConfig<IDbConfig>();
            _dbProvider = new RocksDbProvider(_initConfig.BaseDbPath, dbConfig, _logManager, _initConfig.StoreTraces, _initConfig.StoreReceipts);

            var stateProvider = new StateProvider(
               _dbProvider.StateDb,
               _dbProvider.CodeDb,
               _logManager);

            _stateProvider = stateProvider;





            /* blockchain processing */


            _specProvider = new ChainSpecBasedSpecProvider(_chainSpec);
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, _logManager);
            _txPool = new TxPool(
                new PersistentTxStorage(_dbProvider.PendingTxsDb, _specProvider),
                new PendingTxThresholdValidator(_txPoolConfig.ObsoletePendingTransactionInterval,
                    _txPoolConfig.RemovePendingTransactionInterval), new Timestamp(),
                _ethereumEcdsa, _specProvider, _logManager, _txPoolConfig.RemovePendingTransactionInterval,
                _txPoolConfig.PeerNotificationThreshold);
            _receiptStorage = new PersistentReceiptStorage(_dbProvider.ReceiptsDb, _specProvider, _logManager);

            _blockTree = new BlockTree(
              _dbProvider.BlocksDb,
              _dbProvider.HeadersDb,
              _dbProvider.BlockInfosDb,
              _specProvider,
              _txPool,
              _logManager);
            _recoveryStep = new TxSignaturesRecoveryStep(_ethereumEcdsa, _txPool, _logManager);
            CliqueConfig cliqueConfig = null;
            _snapshotManager = null;
            switch (_chainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _sealer = NullSealEngine.Instance;
                    _sealValidator = NullSealEngine.Instance;
                    _rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _rewardCalculator = NoBlockRewards.Instance;
                    cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _chainSpec.Clique.Period;
                    cliqueConfig.Epoch = _chainSpec.Clique.Epoch;
                    _snapshotManager = new SnapshotManager(cliqueConfig, _dbProvider.BlocksDb, _blockTree, _ethereumEcdsa, _logManager);
                    _sealValidator = new CliqueSealValidator(cliqueConfig, _snapshotManager, _logManager);
                    _recoveryStep = new CompositeDataRecoveryStep(_recoveryStep, new AuthorRecoveryStep(_snapshotManager));
                    if (_initConfig.IsMining)
                    {
                        _sealer = new CliqueSealer(new BasicWallet(_nodeKey), cliqueConfig, _snapshotManager, _nodeKey.Address, _logManager);
                    }
                    else
                    {
                        _sealer = NullSealEngine.Instance;
                    }

                    break;
                case SealEngineType.NethDev:
                    _sealer = NullSealEngine.Instance;
                    _sealValidator = NullSealEngine.Instance;
                    _rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Ethash:
                    _rewardCalculator = new RewardCalculator(_specProvider);
                    var difficultyCalculator = new DifficultyCalculator(_specProvider);
                    if (_initConfig.IsMining)
                    {
                        _sealer = new EthashSealer(new Ethash(_logManager), _logManager);
                    }
                    else
                    {
                        _sealer = NullSealEngine.Instance;
                    }

                    _sealValidator = new EthashSealValidator(_logManager, difficultyCalculator, new Ethash(_logManager));
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_chainSpec.SealEngineType} is not supported in Nethermind");
            }


            _headerValidator = new HeaderValidator(
                _blockTree,
                _sealValidator,
                _specProvider,
                _logManager);

            var ommersValidator = new OmmersValidator(
                _blockTree,
                _headerValidator,
                _logManager);
            var storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                stateProvider,
                _logManager);

            var txValidator = new TxValidator(_specProvider.ChainId);

            _blockValidator = new BlockValidator(
                txValidator,
                _headerValidator,
                ommersValidator,
                _specProvider,
                 _logManager);





            var blockhashProvider = new BlockhashProvider(
                _blockTree, _logManager);

            var virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                _logManager);

            var transactionProcessor = new TransactionProcessor(
                _specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            _blockProcessor = new BlockProcessor(
                _specProvider,
                _blockValidator,
                _rewardCalculator,
                transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _dbProvider.TraceDb,
                stateProvider,
                storageProvider,
                _txPool,
                _receiptStorage,
                _configProvider.GetConfig<ISyncConfig>(),
                _logManager);

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                _blockProcessor,
                _recoveryStep,
                _logManager,
                _initConfig.StoreReceipts,
                _initConfig.StoreTraces);


            string chainFile = Path.Join(Path.GetDirectoryName(_initConfig.ChainSpecPath), "chain.rlp");

            if (!File.Exists(chainFile))
            {
                _logger.Info($"Chain file does not exist: {chainFile}, skipping");
                return;
            }

            var chainFileContent = File.ReadAllBytes(chainFile);
            var context = new Rlp.DecoderContext(chainFileContent);
            var blocks = new List<Block>();
            while (context.ReadNumberOfItemsRemaining() > 0)
            {
                context.PeekNextItem();
                blocks.Add(Rlp.Decode<Block>(context));
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                ProcessBlock(blocks[i]);
            }

        }
        private void ProcessBlock(Block block)
        {
            try
            {
                _logger.Info($"Loading block: {block.Number}");
                _blockTree.SuggestBlock(block);
            }
            catch (InvalidBlockException e)
            {
                _logger.Error($"Invalid block: {block.Hash}, ignoring", e);
            }
        }


        [Todo("find better way to enforce assemblies with config impl are loaded")]
        protected override (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp()
        {
            var app = new CommandLineApplication {Name = "ChainLoader"};
            app.HelpOption("-?|-h|--help");
            var configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);
            var dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "base db path", CommandOptionType.SingleValue);


            foreach (Type configType in _configs)
            {
                foreach (PropertyInfo propertyInfo in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    app.Option($"--{configType.Name}.{propertyInfo.Name}", $"{configType.Name}.{propertyInfo.Name}", CommandOptionType.SingleValue);
                }
            }

            IConfigProvider BuildConfigProvider()
            {
                ConfigProvider configProvider = new ConfigProvider();
                Dictionary<string, string> args = new Dictionary<string, string>();
                foreach (CommandOption commandOption in app.Options)
                {
                    if (commandOption.HasValue())
                    {
                        args.Add(commandOption.LongName, commandOption.Value());
                    }
                }

                IConfigSource argsSource = new ArgsConfigSource(args);
                configProvider.AddSource(argsSource);
                configProvider.AddSource(new EnvConfigSource());
                
                string configFilePath = configFile.HasValue() ? configFile.Value() : _defaultConfigFile;
                var configPathVariable = Environment.GetEnvironmentVariable("NETHERMIND_CONFIG");
                if (!string.IsNullOrWhiteSpace(configPathVariable))
                {
                    configFilePath = configPathVariable;
                }

                if (!Path.HasExtension(configFilePath) && !configFilePath.Contains(Path.DirectorySeparatorChar))
                {
                    string redirectedConfigPath = Path.Combine("configs", string.Concat(configFilePath, ".cfg"));
                    Console.WriteLine($"Redirecting config {configFilePath} to {redirectedConfigPath}");
                    configFilePath = redirectedConfigPath;
                }

                Console.WriteLine($"Reading config file from {configFilePath}");
                configProvider.AddSource(new JsonConfigSource(configFilePath));
                return configProvider;
            }

            string GetBaseDbPath()
            {
                return dbBasePath.HasValue() ? dbBasePath.Value() : null;
            }

            return (app, BuildConfigProvider, GetBaseDbPath);
        }
    }
}