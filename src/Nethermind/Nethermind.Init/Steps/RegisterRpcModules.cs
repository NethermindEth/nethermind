// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.JsonRpc.Modules.Rpc;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins))]
public class RegisterRpcModules : IStep
{
    private readonly INethermindApi _api;
    private readonly IJsonRpcConfig _jsonRpcConfig;

    public RegisterRpcModules(INethermindApi api)
    {
        _api = api;
        _jsonRpcConfig = _api.Config<IJsonRpcConfig>();
    }

    public virtual async Task Execute(CancellationToken cancellationToken)
    {
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptFinder);
        StepDependencyException.ThrowIfNull(_api.BloomStorage);
        StepDependencyException.ThrowIfNull(_api.LogManager);

        if (!_jsonRpcConfig.Enabled)
        {
            return;
        }

        StepDependencyException.ThrowIfNull(_api.FileSystem);
        StepDependencyException.ThrowIfNull(_api.TxPool);
        StepDependencyException.ThrowIfNull(_api.Wallet);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.SyncModeSelector);
        StepDependencyException.ThrowIfNull(_api.TxSender);
        StepDependencyException.ThrowIfNull(_api.StateReader);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.PeerManager);

        _api.RpcModuleProvider = new RpcModuleProvider(_api.FileSystem, _jsonRpcConfig, _api.LogManager);

        IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider;

        // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
        ILogger logger = _api.LogManager.GetClassLogger();

        IInitConfig initConfig = _api.Config<IInitConfig>();
        INetworkConfig networkConfig = _api.Config<INetworkConfig>();

        // lets add threads to support parallel eth_getLogs
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(workerThreads + Environment.ProcessorCount, completionPortThreads + Environment.ProcessorCount);

        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.GasPriceOracle);
        StepDependencyException.ThrowIfNull(_api.EthSyncingInfo);

        RpcLimits.Init(_jsonRpcConfig.RequestQueueLimit);
        RegisterEthRpcModule(rpcModuleProvider);


        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockPreprocessor);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.KeyStore);
        StepDependencyException.ThrowIfNull(_api.PeerPool);
        StepDependencyException.ThrowIfNull(_api.BadBlocksStore);

        ProofModuleFactory proofModuleFactory = new(_api.WorldStateManager, _api.BlockTree, _api.BlockPreprocessor, _api.ReceiptFinder, _api.SpecProvider, _api.LogManager);
        rpcModuleProvider.RegisterBounded(proofModuleFactory, 2, _jsonRpcConfig.Timeout);

        DebugModuleFactory debugModuleFactory = new(
            _api.WorldStateManager,
            _api.DbProvider,
            _api.BlockTree,
            _jsonRpcConfig,
            _api.BlockValidator,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            new ReceiptMigration(_api),
            _api.ConfigProvider,
            _api.SpecProvider,
            _api.SyncModeSelector,
            _api.BadBlocksStore,
            _api.FileSystem,
            _api.LogManager);
        rpcModuleProvider.RegisterBoundedByCpuCount(debugModuleFactory, _jsonRpcConfig.Timeout);

        RegisterTraceRpcModule(rpcModuleProvider);

        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);

        PersonalRpcModule personalRpcModule = new(
            _api.EthereumEcdsa,
            _api.Wallet,
            _api.KeyStore);
        rpcModuleProvider.RegisterSingle<IPersonalRpcModule>(personalRpcModule);

        StepDependencyException.ThrowIfNull(_api.PeerManager);
        StepDependencyException.ThrowIfNull(_api.StaticNodesManager);
        StepDependencyException.ThrowIfNull(_api.Enode);

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

        StepDependencyException.ThrowIfNull(_api.TxPoolInfoProvider);

        TxPoolRpcModule txPoolRpcModule = new(_api.TxPoolInfoProvider, _api.LogManager);
        rpcModuleProvider.RegisterSingle<ITxPoolRpcModule>(txPoolRpcModule);

        StepDependencyException.ThrowIfNull(_api.SyncServer);
        StepDependencyException.ThrowIfNull(_api.EngineSignerStore);

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

        StepDependencyException.ThrowIfNull(_api.ReceiptMonitor);

        JsonRpcLocalStats jsonRpcLocalStats = new(
            _api.Timestamper,
            _jsonRpcConfig,
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

        RpcRpcModule rpcRpcModule = new(rpcModuleProvider.Enabled);
        rpcModuleProvider.RegisterSingle<IRpcRpcModule>(rpcRpcModule);

        if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", rpcModuleProvider.Enabled.OrderBy(x => x))}");
        ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", rpcModuleProvider.Enabled.OrderBy(x => x))}");

        await Task.CompletedTask;
    }

    protected ModuleFactoryBase<IEthRpcModule> CreateEthModuleFactory()
    {
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.TxPool);
        StepDependencyException.ThrowIfNull(_api.TxSender);
        StepDependencyException.ThrowIfNull(_api.Wallet);
        StepDependencyException.ThrowIfNull(_api.StateReader);
        StepDependencyException.ThrowIfNull(_api.GasPriceOracle);
        StepDependencyException.ThrowIfNull(_api.EthSyncingInfo);

        var feeHistoryOracle = new FeeHistoryOracle(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
        _api.DisposeStack.Push(feeHistoryOracle);
        return new EthModuleFactory(
            _api.TxPool,
            _api.TxSender,
            _api.Wallet,
            _api.BlockTree,
            _jsonRpcConfig,
            _api.LogManager,
            _api.StateReader,
            _api,
            _api.SpecProvider,
            _api.ReceiptStorage,
            _api.GasPriceOracle,
            _api.EthSyncingInfo,
            feeHistoryOracle);
    }

    protected virtual void RegisterEthRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = CreateEthModuleFactory();

        rpcModuleProvider.RegisterBounded(ethModuleFactory,
            _jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, _jsonRpcConfig.Timeout);
    }

    protected ModuleFactoryBase<ITraceRpcModule> CreateTraceModuleFactory()
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        return new TraceModuleFactory(
            _api.WorldStateManager,
            _api.BlockTree,
            _jsonRpcConfig,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _api.PoSSwitcher,
            _api.LogManager);
    }

    protected virtual void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        ModuleFactoryBase<ITraceRpcModule> traceModuleFactory = CreateTraceModuleFactory();

        rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, _jsonRpcConfig.Timeout);
    }
}
