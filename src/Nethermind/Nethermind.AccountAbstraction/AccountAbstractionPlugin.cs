using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionPlugin : IConsensusWrapperPlugin
    {
        private IAccountAbstractionConfig _accountAbstractionConfig = null!;
        private UserOperationPool? _userOperationPool;
        private UserOperationSimulator? _userOperationSimulator;
        private ConcurrentDictionary<UserOperation, SimulatedUserOperationContext> _simulatedUserOperations =
            new ConcurrentDictionary<UserOperation, SimulatedUserOperationContext>();
        private INethermindApi _nethermindApi = null!;
        private ILogger _logger;

        public string Name => "Account Abstraction";

        public string Description => "Implements account abstraction via alternative mempool";

        public string Author => "Nethermind";
        
        private UserOperationPool UserOperationPool
        {
            get
            {
                if (_userOperationPool is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    UserOperationSortedPool userOperationSortedPool = new(
                        _accountAbstractionConfig.UserOperationPoolSize,
                        CompareUserOperationsByGasPrice.Default,
                        getFromApi.LogManager);

                    _userOperationPool = new UserOperationPool(
                        _nethermindApi.BlockTree,
                        _nethermindApi.StateProvider,
                        _nethermindApi.BlockchainProcessor,
                        userOperationSortedPool,
                        UserOperationSimulator,
                        new SimulatedUserOperationSource(_simulatedUserOperations),
                        _simulatedUserOperations
                    );
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

                    UserOperationSimulator userOperationSimulator = new(_simulatedUserOperations);
                }
                return _userOperationSimulator;
            }
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _accountAbstractionConfig = _nethermindApi.Config<IAccountAbstractionConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol() => Task.CompletedTask;

        public Task InitRpcModules()
        {
            if (Enabled) 
            {   
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;

                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                rpcConfig.EnableModules(ModuleType.AccountAbstraction);

                AccountAbstractionModuleFactory accountAbstractionModuleFactory = new(
                    UserOperationPool);
                
                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(accountAbstractionModuleFactory, rpcConfig.Timeout);

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

        public Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin, ITxSource? txSource = null)
        {
            UserOperationTxSource userOperationTxSource = new();
            return consensusPlugin.InitBlockProducer(userOperationTxSource);
        }

        public bool Enabled => _accountAbstractionConfig.Enabled;
    }
}
