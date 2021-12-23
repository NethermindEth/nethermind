using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Contracts;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Network;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Mev;
using Nethermind.AccountAbstraction.Bundler;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionPlugin : IConsensusWrapperPlugin
    {
        private IAccountAbstractionConfig _accountAbstractionConfig = null!;
        private Address _create2FactoryAddress = null!;
        private AbiDefinition _entryPointContractAbi = null!;
        private ILogger _logger = null!;

        private INethermindApi _nethermindApi = null!;
        private Address _entryPointContractAddress = null!;
        private UserOperationPool? _userOperationPool;
        private UserOperationSimulator? _userOperationSimulator;
        private UserOperationTxSource? _userOperationTxSource;
        private IBundler? _bundler;

        private MevPlugin MevPlugin => _nethermindApi
            .GetConsensusWrapperPlugins()
            .OfType<MevPlugin>()
            .Single();

        private UserOperationPool UserOperationPool
        {
            get
            {
                if (_userOperationPool is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;
                    UserOperationSortedPool userOperationSortedPool = new(
                        _accountAbstractionConfig.UserOperationPoolSize,
                        CompareUserOperationsByDecreasingGasPrice.Default,
                        getFromApi.LogManager);

                    _userOperationPool = new UserOperationPool(
                        _accountAbstractionConfig,
                        _nethermindApi.BlockTree!,
                        _entryPointContractAddress,
                        _logger,
                        new PaymasterThrottler(BundleMiningEnabled),
                        _nethermindApi.ReceiptStorage!,
                        _nethermindApi.EngineSigner!,
                        _nethermindApi.StateProvider!,
                        _nethermindApi.Timestamper,
                        UserOperationSimulator,
                        userOperationSortedPool);
                }

                return _userOperationPool;
            }
        }

        private UserOperationSimulator UserOperationSimulator
        {
            get
            {
                if (_userOperationSimulator is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    _userOperationSimulator = new UserOperationSimulator(
                        getFromApi.StateProvider!,
                        _entryPointContractAbi,
                        getFromApi.EngineSigner!,
                        _accountAbstractionConfig,
                        _create2FactoryAddress,
                        _entryPointContractAddress,
                        getFromApi.SpecProvider!,
                        getFromApi.BlockTree!,
                        getFromApi.DbProvider!,
                        getFromApi.ReadOnlyTrieStore!,
                        getFromApi.LogManager,
                        getFromApi.BlockPreprocessor);
                }

                return _userOperationSimulator;
            }
        }

        private UserOperationTxSource UserOperationTxSource
        {
            get
            {
                if (_userOperationTxSource is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    _userOperationTxSource = new UserOperationTxSource
                    (
                        UserOperationPool,
                        UserOperationSimulator,
                        _nethermindApi.SpecProvider!,
                        _logger
                    );
                }

                return _userOperationTxSource;
            }
        }

        public string Name => "Account Abstraction";

        public string Description => "Implements account abstraction via alternative mempool (ERC-4337)";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _accountAbstractionConfig = _nethermindApi.Config<IAccountAbstractionConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();

            if (_accountAbstractionConfig.Enabled)
            {
                bool parsed = Address.TryParse(
                    _accountAbstractionConfig.EntryPointContractAddress,
                    out Address? entryPointContractAddress);
                if (!parsed)
                    _logger.Error("Account Abstraction Plugin: EntryPoint contract address could not be parsed");
                else
                {
                    _logger.Info($"Parsed EntryPoint Address: {entryPointContractAddress}");
                    _entryPointContractAddress = entryPointContractAddress!;
                }

                bool parsedCreate2Factory = Address.TryParse(
                    _accountAbstractionConfig.Create2FactoryAddress,
                    out Address? create2FactoryAddress);
                if (!parsedCreate2Factory)
                    _logger.Error("Account Abstraction Plugin: Create2Factory contract address could not be parsed");
                else
                {
                    _logger.Info($"Parsed Create2Factory Address: {create2FactoryAddress}");
                    _create2FactoryAddress = create2FactoryAddress!;
                }

                _entryPointContractAbi = LoadEntryPointContract();
            }

            if (Enabled)
            {
                if (_logger.IsInfo) _logger.Info("  Account Abstraction Plugin: User Operation Mining Enabled");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("  Account Abstraction Plugin: User Operation Mining Disabled");
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_accountAbstractionConfig.Enabled)
            {
                if (_nethermindApi is null) throw new ArgumentNullException(nameof(_nethermindApi));
                if (UserOperationPool is null) throw new ArgumentNullException(nameof(UserOperationPool));

                IProtocolsManager protocolsManager = _nethermindApi.ProtocolsManager ??
                                                     throw new ArgumentNullException(
                                                         nameof(_nethermindApi.ProtocolsManager));
                IMessageSerializationService serializer = _nethermindApi.MessageSerializationService ??
                                                          throw new ArgumentNullException(
                                                              nameof(_nethermindApi.MessageSerializationService));
                INodeStatsManager stats = _nethermindApi.NodeStatsManager ??
                                          throw new ArgumentNullException(nameof(_nethermindApi.NodeStatsManager));
                ILogManager logManager = _nethermindApi.LogManager ??
                                         throw new ArgumentNullException(nameof(_nethermindApi.LogManager));

                serializer.Register(new UserOperationsMessageSerializer());
                protocolsManager.AddProtocol(Protocol.AA,
                    session => new AaProtocolHandler(session, serializer, stats, UserOperationPool, logManager));
                protocolsManager.AddSupportedCapability(new Capability(Protocol.AA, 0));

                if (_logger.IsInfo) _logger.Info("Initialized Account Abstraction network protocol");
            }
            else
            {
                if (_logger.IsInfo) _logger.Info("Skipping Account Abstraction network protocol");
            }

            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_accountAbstractionConfig.Enabled)
            {
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;

                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                rpcConfig.EnableModules(ModuleType.AccountAbstraction);

                AccountAbstractionModuleFactory accountAbstractionModuleFactory = new(UserOperationPool);

                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(accountAbstractionModuleFactory, rpcConfig.Timeout);

                _logger.Info("Checking whether AA AND MEV are enabled");
                if (BundleMiningEnabled && MevPluginEnabled)
                {
                    _logger.Info("Both AA and MEV Enabled!!");
                    _bundler = new MevBundler(
                        new PeriodicBundleTrigger(TimeSpan.FromSeconds(5), _nethermindApi.BlockTree!, _logger),
                        UserOperationTxSource, MevPlugin.BundlePool,
                        _logger
                    );
                }


                if (_logger!.IsInfo) _logger.Info("Account Abstraction RPC plugin enabled");
            }
            else
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Account Abstraction RPC plugin");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            if (!Enabled) throw new InvalidOperationException("Account Abstraction plugin is disabled");

            UInt256 minerBalance = _nethermindApi.StateProvider!.GetBalance(_nethermindApi.EngineSigner!.Address);
            if (minerBalance < 1.Ether())
                _logger.Warn(
                    $"Account Abstraction Plugin: Miner ({_nethermindApi.EngineSigner!.Address}) Ether balance low - {minerBalance / 1.Ether()} Ether < 1 Ether. Increasing balance is recommended");
            else
            {
                if (_logger.IsInfo)
                    _logger.Info(
                        $"Account Abstraction Plugin: Miner ({_nethermindApi.EngineSigner!.Address}) Ether balance adequate - {minerBalance / 1.Ether()} Ether");
            }

            IManualBlockProductionTrigger trigger = new BuildBlocksWhenRequested();

            return consensusPlugin.InitBlockProducer(trigger, UserOperationTxSource);
        }

        public bool MevPluginEnabled => _nethermindApi.Config<IMevConfig>().Enabled;
        public bool BundleMiningEnabled => _accountAbstractionConfig.Enabled && (_nethermindApi.Config<IInitConfig>().IsMining || _nethermindApi.Config<IMiningConfig>().Enabled);
        public bool Enabled => BundleMiningEnabled && !MevPluginEnabled; // IConsensusWrapperPlugin.Enabled

        private AbiDefinition LoadEntryPointContract()
        {
            AbiDefinitionParser parser = new();
            parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
            string json = parser.LoadContract(typeof(EntryPoint));
            return parser.Parse(json);
        }
    }
}
