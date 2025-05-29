// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializePlugins))]
public class RegisterRpcModules : IStep
{
    private readonly INethermindApi _api;
    protected readonly IJsonRpcConfig JsonRpcConfig;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBlocksConfig _blocksConfig;

    public RegisterRpcModules(INethermindApi api, IPoSSwitcher poSSwitcher)
    {
        _api = api;
        JsonRpcConfig = _api.Config<IJsonRpcConfig>();
        _blocksConfig = _api.Config<IBlocksConfig>();
        _poSSwitcher = poSSwitcher;
    }

    public virtual async Task Execute(CancellationToken cancellationToken)
    {
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.LogManager);
        StepDependencyException.ThrowIfNull(_api.Wallet);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.KeyStore);
        StepDependencyException.ThrowIfNull(_api.PeerPool);
        StepDependencyException.ThrowIfNull(_api.ReceiptMonitor);
        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);
        StepDependencyException.ThrowIfNull(_api.TrustedNodesManager);

        if (!JsonRpcConfig.Enabled)
        {
            return;
        }

        IRpcModuleProvider rpcModuleProvider = _api.RpcModuleProvider!;

        // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
        ILogger logger = _api.LogManager.GetClassLogger();

        IInitConfig initConfig = _api.Config<IInitConfig>();
        INetworkConfig networkConfig = _api.Config<INetworkConfig>();

        // lets add threads to support parallel eth_getLogs
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(workerThreads + Environment.ProcessorCount, completionPortThreads + Environment.ProcessorCount);

        RpcLimits.Init(JsonRpcConfig.RequestQueueLimit);
        RegisterEthRpcModule(rpcModuleProvider);

        RegisterProofRpcModule(rpcModuleProvider);

        RegisterDebugRpcModule(rpcModuleProvider);

        RegisterTraceRpcModule(rpcModuleProvider);

        PersonalRpcModule personalRpcModule = new(
            _api.EthereumEcdsa,
            _api.Wallet,
            _api.KeyStore);
        rpcModuleProvider.RegisterSingle<IPersonalRpcModule>(personalRpcModule);

        StepDependencyException.ThrowIfNull(_api.Enode);

        (IApiWithStores getFromApi, IApiWithBlockchain setInApi) = _api.ForInit;

        _api.SubscriptionFactory = new SubscriptionFactory();
        // Register the standard subscription types in the dictionary
        _api.SubscriptionFactory.RegisterStandardSubscriptions(_api.BlockTree, _api.LogManager, _api.SpecProvider, _api.ReceiptMonitor, _api.FilterStore, _api.TxPool, _api.EthSyncingInfo!, _api.PeerPool, _api.RlpxPeer);
        SubscriptionManager subscriptionManager = new(_api.SubscriptionFactory, _api.LogManager);

        AdminRpcModule adminRpcModule = new(
            _api.BlockTree,
            networkConfig,
            _api.PeerPool,
            _api.StaticNodesManager,
            _api.WorldStateManager.GlobalStateReader,
            _api.Enode,
            initConfig.BaseDbPath,
            getFromApi.ChainSpec.Parameters,
            _api.TrustedNodesManager,
            subscriptionManager);
        rpcModuleProvider.RegisterSingle<IAdminRpcModule>(adminRpcModule);

        JsonRpcLocalStats jsonRpcLocalStats = new(
            _api.Timestamper,
            JsonRpcConfig,
            _api.LogManager);

        _api.JsonRpcLocalStats = jsonRpcLocalStats;

        SubscribeRpcModule subscribeRpcModule = new(subscriptionManager);
        rpcModuleProvider.RegisterSingle<ISubscribeRpcModule>(subscribeRpcModule);

        RpcRpcModule rpcRpcModule = new(rpcModuleProvider.Enabled);
        rpcModuleProvider.RegisterSingle<IRpcRpcModule>(rpcRpcModule);

        if (logger.IsDebug) logger.Debug($"RPC modules  : {string.Join(", ", rpcModuleProvider.Enabled.OrderBy(static x => x))}");
        ThisNodeInfo.AddInfo("RPC modules  :", $"{string.Join(", ", rpcModuleProvider.Enabled.OrderBy(static x => x))}");

        await Task.CompletedTask;
    }

    protected virtual void RegisterProofRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptFinder);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        ProofModuleFactory proofModuleFactory = new(
            _api.WorldStateManager,
            _api.ReadOnlyTxProcessingEnvFactory,
            _api.BlockTree,
            _api.BlockPreprocessor,
            _api.ReceiptFinder,
            _api.SpecProvider,
            _api.LogManager);
        rpcModuleProvider.RegisterBounded(proofModuleFactory, 2, JsonRpcConfig.Timeout);
    }

    protected virtual void RegisterDebugRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockPreprocessor);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.KeyStore);
        StepDependencyException.ThrowIfNull(_api.PeerPool);
        StepDependencyException.ThrowIfNull(_api.BadBlocksStore);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        DebugModuleFactory debugModuleFactory = new(
            _api.WorldStateManager,
            _api.DbProvider,
            _api.BlockTree,
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            _blocksConfig.SecondsPerSlot,
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
        rpcModuleProvider.RegisterBoundedByCpuCount(debugModuleFactory, JsonRpcConfig.Timeout);
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

        IBlocksConfig blockConfig = _blocksConfig;
        ulong secondsPerSlot = blockConfig.SecondsPerSlot;

        return new EthModuleFactory(
            _api.TxPool,
            _api.TxSender,
            _api.Wallet,
            _api.BlockTree,
            JsonRpcConfig,
            _api.LogManager,
            _api.StateReader,
            _api,
            _api.SpecProvider,
            _api.ReceiptStorage,
            _api.GasPriceOracle,
            _api.EthSyncingInfo,
            feeHistoryOracle,
            secondsPerSlot);
    }

    protected virtual void RegisterEthRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        ModuleFactoryBase<IEthRpcModule> ethModuleFactory = CreateEthModuleFactory();

        rpcModuleProvider.RegisterBounded(ethModuleFactory,
            JsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, JsonRpcConfig.Timeout);
    }

    protected ModuleFactoryBase<ITraceRpcModule> CreateTraceModuleFactory()
    {
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.DbProvider);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);

        return new TraceModuleFactory(
            _api.WorldStateManager,
            _api.BlockTree,
            JsonRpcConfig,
            _api.CreateBlockchainBridge(),
            _blocksConfig.SecondsPerSlot,
            _api.BlockPreprocessor,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _poSSwitcher,
            _api.LogManager);
    }

    protected virtual void RegisterTraceRpcModule(IRpcModuleProvider rpcModuleProvider)
    {
        ModuleFactoryBase<ITraceRpcModule> traceModuleFactory = CreateTraceModuleFactory();

        rpcModuleProvider.RegisterBoundedByCpuCount(traceModuleFactory, JsonRpcConfig.Timeout);
    }
}
