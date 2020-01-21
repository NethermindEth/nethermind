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
using System.Threading.Tasks;
using Nethermind.Clique;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.Facade;
using Nethermind.Facade.Config;
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
using Nethermind.Runner.Ethereum.Subsystems;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeNetwork))]
    public class RegisterRpcModules : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public RegisterRpcModules(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        { 
            IJsonRpcConfig jsonRpcConfig = _context.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return Task.CompletedTask;
            }

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            if (_context.Logger.IsDebug) _context.Logger.Debug($"Resolving CLI ({nameof(Cli.CliModuleLoader)})");

            IInitConfig initConfig = _context.Config<IInitConfig>();
            INdmConfig ndmConfig = _context.Config<INdmConfig>();
            IRpcConfig rpcConfig = _context.Config<IRpcConfig>();
            if (ndmConfig.Enabled && !(_context.NdmInitializer is null) && ndmConfig.ProxyEnabled)
            {
                EthModuleProxyFactory proxyFactory = new EthModuleProxyFactory(_context.EthJsonRpcClientProxy, _context.Wallet);
                _context.RpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (_context.Logger.IsInfo) _context.Logger.Info("Enabled JSON RPC Proxy for NDM.");
            }
            else
            {
                EthModuleFactory ethModuleFactory = new EthModuleFactory(_context.DbProvider, _context.TxPool, _context.Wallet, _context.BlockTree,
                    _context.EthereumEcdsa, _context.BlockProcessor, _context.ReceiptStorage, _context.SpecProvider, rpcConfig, _context.LogManager);
                _context.RpcModuleProvider.Register(new BoundedModulePool<IEthModule>(8, ethModuleFactory));
            }

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(_context.DbProvider, _context.BlockTree, _context.BlockValidator, _context.RecoveryStep, _context.RewardCalculator, _context.ReceiptStorage, _context.ConfigProvider, _context.SpecProvider, _context.LogManager);
            _context.RpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(8, debugModuleFactory));

            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(_context.DbProvider, _context.TxPool, _context.BlockTree, _context.BlockValidator, _context.EthereumEcdsa, _context.RecoveryStep, _context.RewardCalculator, _context.ReceiptStorage, _context.SpecProvider, rpcConfig, _context.LogManager);
            _context.RpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(8, traceModuleFactory));

            if (_context.SealValidator is CliqueSealValidator)
            {
                CliqueModule cliqueModule = new CliqueModule(_context.LogManager, new CliqueBridge(_context.BlockProducer as ICliqueBlockProducer, _context.SnapshotManager, _context.BlockTree));
                _context.RpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            }

            if (initConfig.EnableUnsecuredDevWallet)
            {
                PersonalBridge personalBridge = new PersonalBridge(_context.EthereumEcdsa, _context.Wallet);
                PersonalModule personalModule = new PersonalModule(personalBridge, _context.LogManager);
                _context.RpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));
            }

            AdminModule adminModule = new AdminModule(_context.PeerManager, _context.StaticNodesManager);
            _context.RpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));

            TxPoolModule txPoolModule = new TxPoolModule(_context.LogManager, _context.TxPoolInfoProvider);
            _context.RpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

            NetModule netModule = new NetModule(_context.LogManager, new NetBridge(_context.Enode, _context.SyncServer, _context.PeerManager));
            _context.RpcModuleProvider.Register(new SingletonModulePool<INetModule>(netModule, true));

            ParityModule parityModule = new ParityModule(_context.EthereumEcdsa, _context.TxPool, _context.BlockTree, _context.ReceiptStorage, _context.LogManager);
            _context.RpcModuleProvider.Register(new SingletonModulePool<IParityModule>(parityModule, true));

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
            return Task.CompletedTask;
        }
        
        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Kafka;
    }
}