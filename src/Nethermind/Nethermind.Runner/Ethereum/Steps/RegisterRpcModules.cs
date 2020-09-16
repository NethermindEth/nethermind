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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Cli.Modules;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Baseline.Config;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Db;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.State;
using Nethermind.Vault.JsonRpc;
using Nethermind.Vault.Config;
using Nethermind.Runner.Ethereum.Steps.Migrations;
using Nethermind.TxPool;
using Nethermind.Vault;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain))]
    public class RegisterRpcModules : IStep
    {
        private readonly NethermindApi _api;

        public RegisterRpcModules(NethermindApi api)
        {
            _api = api;
        }

        public virtual Task Execute(CancellationToken cancellationToken)
        {
            if (_api.RpcModuleProvider == null) throw new StepDependencyException(nameof(_api.RpcModuleProvider));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.Wallet == null) throw new StepDependencyException(nameof(_api.Wallet));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.TxSender == null) throw new StepDependencyException(nameof(_api.TxSender));

            ILogger logger = _api.LogManager.GetClassLogger();
            IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return Task.CompletedTask;
            }

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            if (logger.IsDebug) logger.Debug($"Resolving CLI ({nameof(CliModuleLoader)})");

            IInitConfig initConfig = _api.Config<IInitConfig>();
            INdmConfig ndmConfig = _api.Config<INdmConfig>();
            IJsonRpcConfig rpcConfig = _api.Config<IJsonRpcConfig>();
            IBaselineConfig baselineConfig = _api.Config<IBaselineConfig>();
            IVaultConfig vaultConfig = _api.Config<IVaultConfig>();
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            if (ndmConfig.Enabled && !(_api.NdmInitializer is null) && ndmConfig.ProxyEnabled)
            {
                EthModuleProxyFactory proxyFactory = new EthModuleProxyFactory(_api.EthJsonRpcClientProxy, _api.Wallet);
                _api.RpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (logger.IsInfo) logger.Info("Enabled JSON RPC Proxy for NDM.");
            }
            else
            {
                EthModuleFactory ethModuleFactory = new EthModuleFactory(
                    _api.DbProvider,
                    _api.TxPool,
                    _api.TxSender,
                    _api.Wallet,
                    _api.BlockTree,
                    _api.TrieStore,
                    _api.EthereumEcdsa,
                    _api.MainBlockProcessor,
                    _api.ReceiptFinder,
                    _api.SpecProvider,
                    rpcConfig,
                    _api.BloomStorage,
                    _api.LogManager,
                    initConfig.IsMining);
                _api.RpcModuleProvider.Register(new BoundedModulePool<IEthModule>(8, ethModuleFactory));
            }

            ProofModuleFactory proofModuleFactory = new ProofModuleFactory(
                _api.DbProvider,
                _api.BlockTree,
                _api.TrieStore,
                _api.RecoveryStep, _api.ReceiptFinder, _api.SpecProvider, _api.LogManager);
            
            _api.RpcModuleProvider.Register(new BoundedModulePool<IProofModule>(2, proofModuleFactory));

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(
                _api.DbProvider, 
                _api.BlockTree,
				rpcConfig, 
                _api.BlockValidator, 
                _api.RecoveryStep, 
                _api.RewardCalculatorSource, 
                _api.ReceiptStorage,
                new ReceiptMigration(_api),
                _api.TrieStore, 
                _api.ConfigProvider, 
                _api.SpecProvider, 
                _api.LogManager);
            _api.RpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(8, debugModuleFactory));

            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(
                _api.DbProvider,
                _api.BlockTree,
                _api.TrieStore,
                rpcConfig,
                _api.RecoveryStep,
                _api.RewardCalculatorSource,
                _api.ReceiptStorage,
                _api.SpecProvider,
                _api.LogManager);
            
            _api.RpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(8, traceModuleFactory));
            
            PersonalModule personalModule = new PersonalModule(
                _api.EthereumEcdsa,
                _api.Wallet,
                _api.LogManager);
            
            _api.RpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));

            AdminModule adminModule = new AdminModule(_api.BlockTree, networkConfig, _api.PeerManager, _api.StaticNodesManager, _api.Enode, initConfig.BaseDbPath);
            _api.RpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));

            LogFinder logFinder = new LogFinder(
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.BloomStorage,
                _api.LogManager,
                new ReceiptsRecovery(), 1024);

            if (baselineConfig.Enabled)
            {
                IDbProvider dbProvider = _api.DbProvider!;
                IStateReader stateReader = new StateReader(_api.TrieStore, dbProvider.CodeDb, _api.LogManager);

                BaselineModuleFactory baselineModuleFactory = new BaselineModuleFactory(
                    _api.TxSender,
                    stateReader,
                    logFinder,
                    _api.BlockTree,
                    _api.AbiEncoder,
                    _api.FileSystem,
                    _api.LogManager);

                _api.RpcModuleProvider.Register(new SingletonModulePool<IBaselineModule>(baselineModuleFactory, true));
                if (logger?.IsInfo ?? false) logger!.Info($"Baseline RPC Module has been enabled");
            }

            TxPoolModule txPoolModule = new TxPoolModule(_api.BlockTree, _api.TxPoolInfoProvider, _api.LogManager);
            _api.RpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

            
            if (vaultConfig.Enabled)
            {
                VaultService vaultService = new VaultService(vaultConfig, _api.LogManager);
                VaultModule vaultModule = new VaultModule(vaultService, _api.LogManager);
                _api.RpcModuleProvider.Register(new SingletonModulePool<IVaultModule>(vaultModule, true));
                if (logger?.IsInfo ?? false) logger!.Info($"Vault RPC Module has been enabled");
            }

            NetModule netModule = new NetModule(_api.LogManager, new NetBridge(_api.Enode, _api.SyncServer));
            _api.RpcModuleProvider.Register(new SingletonModulePool<INetModule>(netModule, true));

            ParityModule parityModule = new ParityModule(
                _api.EthereumEcdsa,
                _api.TxPool,
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.Enode,
                _api.EngineSignerStore,
                _api.KeyStore,
                _api.LogManager);

            _api.RpcModuleProvider.Register(new SingletonModulePool<IParityModule>(parityModule, true));
            
            return Task.CompletedTask;
        }
    }
}
