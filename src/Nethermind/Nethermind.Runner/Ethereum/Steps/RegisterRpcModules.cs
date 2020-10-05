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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
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
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Runner.Ethereum.Steps.Migrations;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins))]
    public class RegisterRpcModules : IStep
    {
        private readonly INethermindApi _api;

        public RegisterRpcModules(INethermindApi api)
        {
            _api = api;
        }

        public virtual async Task Execute(CancellationToken cancellationToken)
        {
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.ReceiptFinder == null) throw new StepDependencyException(nameof(_api.ReceiptFinder));
            if (_api.BloomStorage == null) throw new StepDependencyException(nameof(_api.BloomStorage));
            if (_api.LogManager == null) throw new StepDependencyException(nameof(_api.LogManager));
            

            LogFinder logFinder = new LogFinder(
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.BloomStorage,
                _api.LogManager,
                new ReceiptsRecovery(), 
                1024);

            _api.LogFinder = logFinder;

            IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return;
            }
            
            if (_api.RpcModuleProvider == null) throw new StepDependencyException(nameof(_api.RpcModuleProvider));
            if (_api.FileSystem == null) throw new StepDependencyException(nameof(_api.FileSystem));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.Wallet == null) throw new StepDependencyException(nameof(_api.Wallet));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.TxSender == null) throw new StepDependencyException(nameof(_api.TxSender));
            if (_api.StateReader == null) throw new StepDependencyException(nameof(_api.StateReader));
            
            _api.RpcModuleProvider ??= jsonRpcConfig.Enabled
                ? new RpcModuleProvider(_api.FileSystem, jsonRpcConfig, _api.LogManager)
                : (IRpcModuleProvider)NullModuleProvider.Instance;

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            ILogger logger = _api.LogManager.GetClassLogger();

            IInitConfig initConfig = _api.Config<IInitConfig>();
            IJsonRpcConfig rpcConfig = _api.Config<IJsonRpcConfig>();
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();
            
            // lets add threads to support parallel eth_getLogs
            ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
            ThreadPool.SetMinThreads(workerThreads + Environment.ProcessorCount, completionPortThreads + Environment.ProcessorCount);
            
            EthModuleFactory ethModuleFactory = new EthModuleFactory(
                _api.TxPool,
                _api.TxSender,
                _api.Wallet,
                _api.BlockTree,
                _api.Config<IJsonRpcConfig>(),
                _api.LogManager,
                _api.StateReader,
                _api);
            _api.RpcModuleProvider.Register(new BoundedModulePool<IEthModule>(ethModuleFactory, 8));
            

            ProofModuleFactory proofModuleFactory = new ProofModuleFactory(_api.DbProvider, _api.BlockTree, _api.RecoveryStep, _api.ReceiptFinder, _api.SpecProvider, _api.LogManager);
            _api.RpcModuleProvider.Register(new BoundedModulePool<IProofModule>(proofModuleFactory, 2));

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(
                _api.DbProvider, 
                _api.BlockTree,
				rpcConfig, 
                _api.BlockValidator, 
                _api.RecoveryStep, 
                _api.RewardCalculatorSource, 
                _api.ReceiptStorage,
                new ReceiptMigration(_api), 
                _api.ConfigProvider, 
                _api.SpecProvider, 
                _api.LogManager);
            _api.RpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(debugModuleFactory, 8));

            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(
                _api.DbProvider,
                _api.BlockTree,
                rpcConfig,
                _api.RecoveryStep,
                _api.RewardCalculatorSource, 
                _api.ReceiptStorage,
                _api.SpecProvider,
                _api.LogManager);
            _api.RpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(traceModuleFactory, 8));
            
            PersonalModule personalModule = new PersonalModule(
                _api.EthereumEcdsa,
                _api.Wallet,
                _api.LogManager);
            _api.RpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));

            AdminModule adminModule = new AdminModule(
                _api.BlockTree,
                networkConfig,
                _api.PeerManager,
                _api.StaticNodesManager,
                _api.Enode,
                initConfig.BaseDbPath);
            _api.RpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));

            TxPoolModule txPoolModule = new TxPoolModule(_api.BlockTree, _api.TxPoolInfoProvider, _api.LogManager);
            _api.RpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

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

            Web3Module web3Module = new Web3Module(_api.LogManager);
            _api.RpcModuleProvider.Register(new SingletonModulePool<IWeb3Module>(web3Module, true));
            
            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                await plugin.InitRpcModules();
                // nie dziala
            }
            
            if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", _api.RpcModuleProvider.Enabled.OrderBy(x => x))}");
            ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", _api.RpcModuleProvider.Enabled.OrderBy(x => x))}");
        }
    }
}
