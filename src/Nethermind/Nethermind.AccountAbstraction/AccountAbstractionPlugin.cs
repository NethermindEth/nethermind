using System;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Contracts;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Network;
using Nethermind.AccountAbstraction.Source;
using Nethermind.AccountAbstraction.Bundler;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Contracts.Json;
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

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionPlugin : INethermindPlugin
    {
        private IAccountAbstractionConfig _accountAbstractionConfig = null!;
        private Address _create2FactoryAddress = null!;
        private AbiDefinition _entryPointContractAbi = null!;
        private ILogger _logger = null!;

        private INethermindApi _nethermindApi = null!;
        private Address _entryPointContractAddress = null!;
        private UserOperationPool? _userOperationPool;
        private UserOperationSimulator? _userOperationSimulator;
        private ITxBundler? _txBundler;

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
                        new PaymasterThrottler(_accountAbstractionConfig.BundlingEnabled),
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

                if (_accountAbstractionConfig.BundlingEnabled)
                { 
                    _txBundler = InitTxBundler();
                    _txBundler.Start();
                }
            }

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            if (_nethermindApi is null) throw new ArgumentNullException(nameof(_nethermindApi));
            if (UserOperationPool is null) throw new ArgumentNullException(nameof(UserOperationPool));

            IProtocolsManager protocolsManager = _nethermindApi.ProtocolsManager ?? throw new ArgumentNullException(nameof(_nethermindApi.ProtocolsManager));
            IMessageSerializationService serializer = _nethermindApi.MessageSerializationService ?? throw new ArgumentNullException(nameof(_nethermindApi.MessageSerializationService));
            INodeStatsManager stats = _nethermindApi.NodeStatsManager ?? throw new ArgumentNullException(nameof(_nethermindApi.NodeStatsManager));
            ILogManager logManager = _nethermindApi.LogManager ?? throw new ArgumentNullException(nameof(_nethermindApi.LogManager));

            protocolsManager.AddProtocol(Protocol.AA, session => new AaProtocolHandler(session, serializer, stats, UserOperationPool, logManager));
            protocolsManager.AddSupportedCapability(new Capability(Protocol.AA, 0));
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_accountAbstractionConfig.Enabled)
            {
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;

                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                rpcConfig.EnableModules(ModuleType.AccountAbstraction);

                AccountAbstractionModuleFactory accountAbstractionModuleFactory = new(
                    UserOperationPool);

                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(accountAbstractionModuleFactory,
                    rpcConfig.Timeout);

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

        public ITxBundler InitTxBundler()
        {
            UInt256 bundlerBalance = _nethermindApi.StateProvider!.GetBalance(_nethermindApi.EngineSigner!.Address);
            if (bundlerBalance < 1.Ether())
                _logger.Warn(
                    $"Account Abstraction Plugin: Bundler ({_nethermindApi.EngineSigner!.Address}) Ether balance low - {bundlerBalance / 1.Ether()} Ether < 1 Ether. Increasing balance is recommended");
            else
            {
                if (_logger.IsInfo)
                    _logger.Info(
                        $"Account Abstraction Plugin: Bundler ({_nethermindApi.EngineSigner!.Address}) Ether balance adequate - {bundlerBalance / 1.Ether()} Ether");
            }

            ITxBundlingTrigger trigger = new TxBundleRegularlyTrigger();
            ITxBundleSource txBundleSource =
                new UserOperationTxSource(UserOperationPool, UserOperationSimulator, _nethermindApi.SpecProvider!, _logger);
            IGasLimitProvider gasLimitProvider = new GasLimitProviderAvg(_nethermindApi.BlockTree!);

            if (_accountAbstractionConfig.UseFlashbots)
                return new TxBundlerFlashbots(
                    trigger, txBundleSource, gasLimitProvider,
                    _nethermindApi.BlockTree!, _nethermindApi.EngineSigner, _logger,
                    _accountAbstractionConfig.FlashbotsEndpoint
                );

            return new TxBundler(
                trigger, txBundleSource, gasLimitProvider,
                _nethermindApi.TxSender!, _nethermindApi.BlockTree!
            );
        }

        private AbiDefinition LoadEntryPointContract()
        {
            AbiDefinitionParser parser = new();
            parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
            string json = parser.LoadContract(typeof(EntryPoint));
            return parser.Parse(json);
        }
    }
}
