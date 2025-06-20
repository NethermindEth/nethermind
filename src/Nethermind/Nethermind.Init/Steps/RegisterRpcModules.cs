// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore), typeof(InitializeBlockchain), typeof(InitializeBlockProducer), typeof(InitializePlugins))]
public class RegisterRpcModules : IStep
{
    private readonly INethermindApi _api;
    protected readonly IJsonRpcConfig JsonRpcConfig;

    public RegisterRpcModules(INethermindApi api)
    {
        _api = api;
        JsonRpcConfig = _api.Config<IJsonRpcConfig>();
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
}
