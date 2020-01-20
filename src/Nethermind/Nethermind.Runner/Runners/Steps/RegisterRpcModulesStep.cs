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
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Clique;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
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
using Nethermind.PubSub;
using Nethermind.PubSub.Kafka;
using Nethermind.PubSub.Kafka.Avro;

namespace Nethermind.Runner.Runners.Steps
{
    [RunnerStep]
    public class RegisterRpcModulesStep : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public RegisterRpcModulesStep(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        { 
            IJsonRpcConfig jsonRpcConfig = _context._configProvider.GetConfig<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return Task.CompletedTask;
            }

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            if (_context.Logger.IsDebug) _context.Logger.Debug($"Resolving CLI ({nameof(Cli.CliModuleLoader)})");

            var ndmConfig = _context._configProvider.GetConfig<INdmConfig>();
            var rpcConfig = _context._configProvider.GetConfig<IRpcConfig>();
            if (ndmConfig.Enabled && !(_context._ndmInitializer is null) && ndmConfig.ProxyEnabled)
            {
                var proxyFactory = new EthModuleProxyFactory(_context._ethJsonRpcClientProxy, _context._wallet);
                _context._rpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
                if (_context.Logger.IsInfo) _context.Logger.Info("Enabled JSON RPC Proxy for NDM.");
            }
            else
            {
                EthModuleFactory ethModuleFactory = new EthModuleFactory(_context._dbProvider, _context._txPool, _context._wallet, _context.BlockTree,
                    _context._ethereumEcdsa, _context._blockProcessor, _context._receiptStorage, _context.SpecProvider, rpcConfig, _context.LogManager);
                _context._rpcModuleProvider.Register(new BoundedModulePool<IEthModule>(8, ethModuleFactory));
            }

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(_context._dbProvider, _context.BlockTree, _context._blockValidator, _context._recoveryStep, _context._rewardCalculator, _context._receiptStorage, _context._configProvider, _context.SpecProvider, _context.LogManager);
            _context._rpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(8, debugModuleFactory));

            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(_context._dbProvider, _context._txPool, _context.BlockTree, _context._blockValidator, _context._ethereumEcdsa, _context._recoveryStep, _context._rewardCalculator, _context._receiptStorage, _context.SpecProvider, rpcConfig, _context.LogManager);
            _context._rpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(8, traceModuleFactory));

            if (_context._sealValidator is CliqueSealValidator)
            {
                CliqueModule cliqueModule = new CliqueModule(_context.LogManager, new CliqueBridge(_context._blockProducer as ICliqueBlockProducer, _context._snapshotManager, _context.BlockTree));
                _context._rpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            }

            if (_context._initConfig.EnableUnsecuredDevWallet)
            {
                PersonalBridge personalBridge = new PersonalBridge(_context._ethereumEcdsa, _context._wallet);
                PersonalModule personalModule = new PersonalModule(personalBridge, _context.LogManager);
                _context._rpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));
            }

            AdminModule adminModule = new AdminModule(_context.PeerManager, _context._staticNodesManager);
            _context._rpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));

            TxPoolModule txPoolModule = new TxPoolModule(_context.LogManager, _context._txPoolInfoProvider);
            _context._rpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

            NetModule netModule = new NetModule(_context.LogManager, new NetBridge(_context._enode, _context._syncServer, _context.PeerManager));
            _context._rpcModuleProvider.Register(new SingletonModulePool<INetModule>(netModule, true));

            ParityModule parityModule = new ParityModule(_context._ethereumEcdsa, _context._txPool, _context.BlockTree, _context._receiptStorage, _context.LogManager);
            _context._rpcModuleProvider.Register(new SingletonModulePool<IParityModule>(parityModule, true));

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
            return Task.CompletedTask;
        }
        
        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Kafka;
    }
}