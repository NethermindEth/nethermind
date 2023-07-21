// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.JsonRpc.Modules.Witness;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Rpc;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins), typeof(InitializeBlockProducer))]
    public class RegisterRpcModules : IStep
    {
        private readonly INethermindApi _api;

        public RegisterRpcModules(INethermindApi api)
        {
            _api = api;
        }

        public virtual async Task Execute(CancellationToken cancellationToken)
        {
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.ReceiptFinder is null) throw new StepDependencyException(nameof(_api.ReceiptFinder));
            if (_api.BloomStorage is null) throw new StepDependencyException(nameof(_api.BloomStorage));
            if (_api.LogManager is null) throw new StepDependencyException(nameof(_api.LogManager));

            IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return;
            }

            if (_api.FileSystem is null) throw new StepDependencyException(nameof(_api.FileSystem));
            if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.Wallet is null) throw new StepDependencyException(nameof(_api.Wallet));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.SyncModeSelector is null) throw new StepDependencyException(nameof(_api.SyncModeSelector));
            if (_api.TxSender is null) throw new StepDependencyException(nameof(_api.TxSender));
            if (_api.StateReader is null) throw new StepDependencyException(nameof(_api.StateReader));
            if (_api.PeerManager is null) throw new StepDependencyException(nameof(_api.PeerManager));

            if (jsonRpcConfig.Enabled)
            {
                _api.RpcModuleProvider = new RpcModuleProvider(_api.FileSystem, jsonRpcConfig, _api.LogManager);
            }
            else
            {
                _api.RpcModuleProvider ??= NullModuleProvider.Instance;
            }

            IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider;

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            ILogger logger = _api.LogManager.GetClassLogger();

            IInitConfig initConfig = _api.Config<IInitConfig>();
            IJsonRpcConfig rpcConfig = _api.Config<IJsonRpcConfig>();
            INetworkConfig networkConfig = _api.Config<INetworkConfig>();

            // lets add threads to support parallel eth_getLogs
            ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
            ThreadPool.SetMinThreads(workerThreads + Environment.ProcessorCount, completionPortThreads + Environment.ProcessorCount);

            if (_api.ReceiptStorage is null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
            if (_api.GasPriceOracle is null) throw new StepDependencyException(nameof(_api.GasPriceOracle));
            if (_api.EthSyncingInfo is null) throw new StepDependencyException(nameof(_api.EthSyncingInfo));
            if (_api.ReadOnlyTrieStore is null) throw new StepDependencyException(nameof(_api.ReadOnlyTrieStore));

            EthModuleFactory ethModuleFactory = new(
                _api.TxPool,
                _api.TxSender,
                _api.Wallet,
                _api.BlockTree,
                rpcConfig,
                _api.LogManager,
                _api.StateReader,
                _api,
                _api.SpecProvider,
                _api.ReceiptStorage,
                _api.GasPriceOracle,
                _api.EthSyncingInfo);

            RpcLimits.Init(rpcConfig.RequestQueueLimit);
            rpcModuleProvider.RegisterBounded(ethModuleFactory, rpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, rpcConfig.Timeout, _api.LogManager, rpcConfig.RequestQueueLimit);

            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.BlockPreprocessor is null) throw new StepDependencyException(nameof(_api.BlockPreprocessor));
            if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.KeyStore is null) throw new StepDependencyException(nameof(_api.KeyStore));
            if (_api.PeerPool is null) throw new StepDependencyException(nameof(_api.PeerPool));
            if (_api.WitnessRepository is null) throw new StepDependencyException(nameof(_api.WitnessRepository));

            ProofModuleFactory proofModuleFactory = new(_api.DbProvider, _api.BlockTree, _api.ReadOnlyTrieStore, _api.BlockPreprocessor, _api.ReceiptFinder, _api.SpecProvider, _api.LogManager);
            rpcModuleProvider.RegisterBounded(proofModuleFactory, 2, rpcConfig.Timeout, _api.LogManager);

            DebugModuleFactory debugModuleFactory = new(
                _api.DbProvider,
                _api.BlockTree,
                rpcConfig,
                _api.BlockValidator,
                _api.BlockPreprocessor,
                _api.RewardCalculatorSource,
                _api.ReceiptStorage,
                new ReceiptMigration(_api),
                _api.ReadOnlyTrieStore,
                _api.ConfigProvider,
                _api.SpecProvider,
                _api.SyncModeSelector,
                _api.LogManager);
            rpcModuleProvider.RegisterBoundedByCpuCount(debugModuleFactory, rpcConfig.Timeout, _api.LogManager);

            TraceModuleFactory traceModuleFactory = new(
                _api.DbProvider,
                _api.BlockTree,
                _api.ReadOnlyTrieStore,
                rpcConfig,
                _api.BlockPreprocessor,
                _api.RewardCalculatorSource,
                _api.ReceiptStorage,
                _api.SpecProvider,
                _api.PoSSwitcher,
                _api.LogManager);

            rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, rpcConfig.Timeout, _api.LogManager);

            if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.Wallet is null) throw new StepDependencyException(nameof(_api.Wallet));

            PersonalRpcModule personalRpcModule = new(
                _api.EthereumEcdsa,
                _api.Wallet,
                _api.KeyStore);
            rpcModuleProvider.RegisterSingle<IPersonalRpcModule>(personalRpcModule);

            if (_api.PeerManager is null) throw new StepDependencyException(nameof(_api.PeerManager));
            if (_api.StaticNodesManager is null) throw new StepDependencyException(nameof(_api.StaticNodesManager));
            if (_api.Enode is null) throw new StepDependencyException(nameof(_api.Enode));

            ManualPruningTrigger pruningTrigger = new();
            _api.PruningTrigger.Add(pruningTrigger);
            AdminRpcModule adminRpcModule = new(
                _api.BlockTree,
                networkConfig,
                _api.PeerPool,
                _api.StaticNodesManager,
                _api.Enode,
                initConfig.BaseDbPath,
                pruningTrigger);
            rpcModuleProvider.RegisterSingle<IAdminRpcModule>(adminRpcModule);

            if (_api.TxPoolInfoProvider is null) throw new StepDependencyException(nameof(_api.TxPoolInfoProvider));

            TxPoolRpcModule txPoolRpcModule = new(_api.TxPoolInfoProvider, _api.LogManager);
            rpcModuleProvider.RegisterSingle<ITxPoolRpcModule>(txPoolRpcModule);

            if (_api.SyncServer is null) throw new StepDependencyException(nameof(_api.SyncServer));
            if (_api.EngineSignerStore is null) throw new StepDependencyException(nameof(_api.EngineSignerStore));

            NetRpcModule netRpcModule = new(_api.LogManager, new NetBridge(_api.Enode, _api.SyncServer));
            rpcModuleProvider.RegisterSingle<INetRpcModule>(netRpcModule);

            ParityRpcModule parityRpcModule = new(
                _api.EthereumEcdsa,
                _api.TxPool,
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.Enode,
                _api.EngineSignerStore,
                _api.KeyStore,
                _api.SpecProvider,
                _api.PeerManager);
            rpcModuleProvider.RegisterSingle<IParityRpcModule>(parityRpcModule);

            WitnessRpcModule witnessRpcModule = new(_api.WitnessRepository, _api.BlockTree);
            rpcModuleProvider.RegisterSingle<IWitnessRpcModule>(witnessRpcModule);

            if (_api.ReceiptMonitor is null) throw new StepDependencyException(nameof(_api.ReceiptMonitor));

            JsonRpcLocalStats jsonRpcLocalStats = new(
                _api.Timestamper,
                jsonRpcConfig,
                _api.LogManager);

            _api.JsonRpcLocalStats = jsonRpcLocalStats;

            SubscriptionFactory subscriptionFactory = new(
                _api.LogManager,
                _api.BlockTree,
                _api.TxPool,
                _api.ReceiptMonitor,
                _api.FilterStore,
                _api.EthSyncingInfo!,
                _api.SpecProvider,
                rpcModuleProvider.Serializer);

            _api.SubscriptionFactory = subscriptionFactory;

            SubscriptionManager subscriptionManager = new(subscriptionFactory, _api.LogManager);

            SubscribeRpcModule subscribeRpcModule = new(subscriptionManager);
            rpcModuleProvider.RegisterSingle<ISubscribeRpcModule>(subscribeRpcModule);

            Web3RpcModule web3RpcModule = new(_api.LogManager);
            rpcModuleProvider.RegisterSingle<IWeb3RpcModule>(web3RpcModule);

            EvmRpcModule evmRpcModule = new(_api.ManualBlockProductionTrigger);
            rpcModuleProvider.RegisterSingle<IEvmRpcModule>(evmRpcModule);

            foreach (INethermindPlugin plugin in _api.Plugins)
            {
                await plugin.InitRpcModules();
            }

            RpcRpcModule rpcRpcModule = new(rpcModuleProvider.Enabled);
            rpcModuleProvider.RegisterSingle<IRpcRpcModule>(rpcRpcModule);

            if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", rpcModuleProvider.Enabled.OrderBy(x => x))}");
            ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", rpcModuleProvider.Enabled.OrderBy(x => x))}");
        }
    }
}
