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
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Baseline.Config;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Vault.JsonRpc;
using Nethermind.Vault.Config;
using Nethermind.Runner.Ethereum.Steps.Migrations;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain))]
    public class RegisterRpcModules : IStep
    {
        private readonly EthereumRunnerContext _context;

        public RegisterRpcModules(EthereumRunnerContext context)
        {
            _context = context;
        }

        public virtual Task Execute(CancellationToken cancellationToken)
        {
            if (_context.RpcModuleProvider == null) throw new StepDependencyException(nameof(_context.RpcModuleProvider));
            if (_context.TxPool == null) throw new StepDependencyException(nameof(_context.TxPool));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.Wallet == null) throw new StepDependencyException(nameof(_context.Wallet));
            if (_context.SpecProvider == null) throw new StepDependencyException(nameof(_context.SpecProvider));

            ILogger logger = _context.LogManager.GetClassLogger();
            IJsonRpcConfig jsonRpcConfig = _context.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return Task.CompletedTask;
            }

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            if (logger.IsDebug) logger.Debug($"Resolving CLI ({nameof(CliModuleLoader)})");

            IInitConfig initConfig = _context.Config<IInitConfig>();
            INdmConfig ndmConfig = _context.Config<INdmConfig>();
            IJsonRpcConfig rpcConfig = _context.Config<IJsonRpcConfig>();
            IBaselineConfig baselineConfig = _context.Config<IBaselineConfig>();
            IVaultConfig vaultConfig = _context.Config<IVaultConfig>();
            INetworkConfig networkConfig = _context.Config<INetworkConfig>();
            if (ndmConfig.Enabled && !(_context.NdmInitializer is null) && ndmConfig.ProxyEnabled)
            {
                EthModuleProxyFactory proxyFactory = new EthModuleProxyFactory(_context.EthJsonRpcClientProxy, _context.Wallet);
                _context.RpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (logger.IsInfo) logger.Info("Enabled JSON RPC Proxy for NDM.");
            }
            else
            {
                EthModuleFactory ethModuleFactory = new EthModuleFactory(_context.DbProvider, _context.TxPool, _context.Wallet, rpcConfig, _context.BlockTree,
                    _context.EthereumEcdsa, _context.MainBlockProcessor, _context.ReceiptFinder, _context.SpecProvider, rpcConfig, _context.BloomStorage, _context.LogManager, initConfig.IsMining);
                _context.RpcModuleProvider.Register(new BoundedModulePool<IEthModule>(8, ethModuleFactory));
            }

            ProofModuleFactory proofModuleFactory = new ProofModuleFactory(_context.DbProvider, _context.BlockTree, _context.RecoveryStep, _context.ReceiptFinder, _context.SpecProvider, _context.LogManager);
            _context.RpcModuleProvider.Register(new BoundedModulePool<IProofModule>(2, proofModuleFactory));

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(
                _context.DbProvider, 
                _context.BlockTree,
				rpcConfig, 
                _context.BlockValidator, 
                _context.RecoveryStep, 
                _context.RewardCalculatorSource, 
                _context.ReceiptStorage,
                new ReceiptMigration(_context), 
                _context.ConfigProvider, 
                _context.SpecProvider, 
                _context.LogManager);
            _context.RpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(8, debugModuleFactory));

            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(_context.DbProvider, _context.BlockTree, rpcConfig, _context.RecoveryStep, _context.RewardCalculatorSource, _context.ReceiptStorage, _context.SpecProvider, _context.LogManager);
            _context.RpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(8, traceModuleFactory));

            PersonalBridge personalBridge = new PersonalBridge(_context.EthereumEcdsa, _context.Wallet);
            PersonalModule personalModule = new PersonalModule(personalBridge, _context.LogManager);
            _context.RpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));

            AdminModule adminModule = new AdminModule(_context.BlockTree, networkConfig, _context.PeerManager, _context.StaticNodesManager, _context.Enode, initConfig.BaseDbPath);
            _context.RpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));

            LogFinder logFinder = new LogFinder(
                _context.BlockTree,
                _context.ReceiptFinder,
                _context.BloomStorage,
                _context.LogManager,
                new ReceiptsRecovery(), 1024);

            if (baselineConfig.Enabled)
            {
                IDbProvider dbProvider = _context.DbProvider!;
                IStateReader stateReader = new StateReader(dbProvider.StateDb, dbProvider.CodeDb, _context.LogManager);

                BaselineModuleFactory baselineModuleFactory = new BaselineModuleFactory(
                    _context.TxPool,
                    stateReader,
                    logFinder,
                    _context.BlockTree,
                    _context.AbiEncoder,
                    _context.Wallet,
                    _context.SpecProvider,
                    _context.FileSystem,
                    _context.LogManager);

                _context.RpcModuleProvider.Register(new SingletonModulePool<IBaselineModule>(baselineModuleFactory, true));
                if (logger?.IsInfo ?? false) logger!.Info($"Baseline RPC Module has been enabled");
            }

            // commented out because of temporary strange build issues on the build server
            // IDepositConfig depositConfig = _context.Config<IDepositConfig>();
            // if (depositConfig.DepositContractAddress != null)
            // {
            //     TxPoolBridge txPoolBridge = new TxPoolBridge(
            //         _context.TxPool, new WalletTxSigner(_context.Wallet, _context.SpecProvider.ChainId), _context.Timestamper);
            //     DepositModule depositModule = new DepositModule(txPoolBridge, logFinder, depositConfig, _context.LogManager);
            //     _context.RpcModuleProvider.Register(new SingletonModulePool<IDepositModule>(depositModule, true));

            TxPoolModule txPoolModule = new TxPoolModule(_context.BlockTree, _context.TxPoolInfoProvider, _context.LogManager);
            _context.RpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

            if (vaultConfig.Enabled)
            {
                VaultModule vaultModule = new VaultModule(vaultConfig, _context.LogManager);
                _context.RpcModuleProvider.Register(new SingletonModulePool<IVaultModule>(vaultModule, true));
                if (logger?.IsInfo ?? false) logger!.Info($"Vault RPC Module has been enabled");
            }

            NetModule netModule = new NetModule(_context.LogManager, new NetBridge(_context.Enode, _context.SyncServer));
            _context.RpcModuleProvider.Register(new SingletonModulePool<INetModule>(netModule, true));

            ParityModule parityModule = new ParityModule(
                _context.EthereumEcdsa,
                _context.TxPool,
                _context.BlockTree,
                _context.ReceiptFinder,
                _context.Enode,
                _context.Signer,
                _context.KeyStore,
                _context.LogManager);

            _context.RpcModuleProvider.Register(new SingletonModulePool<IParityModule>(parityModule, true));
            
            return Task.CompletedTask;
        }
    }
}
