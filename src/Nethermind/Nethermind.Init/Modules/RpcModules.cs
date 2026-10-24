// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Simulate;
using Nethermind.State.OverridableEnv;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.LogIndex;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Sockets;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class RpcModules(IJsonRpcConfig jsonRpcConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Registered to lose: the flat-history module, when loaded, supplies the real one and this must not beat it.
        builder.RegisterInstance(NullPrefixStateSeedSource.Instance)
            .As<IPrefixStateSeedSource>()
            .ExternallyOwned()
            .PreserveExistingDefaults();
        builder.AddDecorator<IPrefixStateSeedSource, BlockAccessListPrefixStateSeedSource>();

        builder
            .AddSingleton<IEthSyncingInfo, EthSyncingInfo>()
            .AddSingleton<IRpcModuleProvider, RpcModuleProvider>()
            // Resolved by StartRpc so plugins can decorate it. A decorator sees a request before its method is checked
            // against the endpoint and authentication; one that answers by itself must check with IRpcModuleProvider first.
            .AddSingleton<IJsonRpcService, JsonRpcService>()
            .AddSingleton<IJsonRpcLocalStats, JsonRpcLocalStats>()
            .AddSingleton<IWebSocketsManager, WebSocketsManager>()

            // Smallish RPCs
            .AddSingleton<INetBridge, NetBridge>()
            .RegisterSingletonJsonRpcModule<INetRpcModule, NetRpcModule>()
            .RegisterSingletonJsonRpcModule<IParityRpcModule, ParityRpcModule>()
            .RegisterSingletonJsonRpcModule<IWeb3RpcModule, Web3RpcModule>()
            .RegisterSingletonJsonRpcModule<IPersonalRpcModule, PersonalRpcModule>()
            .RegisterSingletonJsonRpcModule<IRpcRpcModule, RpcRpcModule>()
            .RegisterSingletonJsonRpcModule<ILogIndexRpcModule, LogIndexRpcModule>()
            .RegisterSingletonJsonRpcModule<IEvmRpcModule, EvmRpcModule>()

            // Txpool rpc
            .RegisterSingletonJsonRpcModule<ITxPoolRpcModule, TxPoolRpcModule>()
                .AddSingleton<ITxPoolInfoProvider, IChainHeadInfoProvider, ITxPool>(
                    (chainHeadInfoProvider, txPool) => new TxPoolInfoProvider(chainHeadInfoProvider, txPool))

            // Subscriptions
            .RegisterBoundedJsonRpcModule<ISubscribeRpcModule, AutoRpcModuleFactory<ISubscribeRpcModule>>(2, jsonRpcConfig.Timeout)
                .AddScoped<ISubscribeRpcModule, SubscribeRpcModule>()
                .AddSingleton<IReceiptMonitor, ReceiptCanonicalityMonitor>()
                .AddSingleton<ISubscriptionFactory, SubscriptionFactory>()
                .AddSingleton<ISubscriptionManager, SubscriptionManager>()

            // Admin
            .RegisterBoundedJsonRpcModule<IAdminRpcModule, AutoRpcModuleFactory<IAdminRpcModule>>(2, jsonRpcConfig.Timeout)
                .AddScoped<IAdminRpcModule>(CreateAdminRpcModule)

            // Eth and its dependencies
            .AddSingleton<IBlockForRpcFactory, BlockForRpcFactory>()
            .AddSingleton<HashesOnlyBlockReader>()
            .AddSingleton<IBatchConcurrencyStrategy, EthCallBatchConcurrency>()
            .RegisterBoundedJsonRpcModule<IEthRpcModule, EthModuleFactory>(jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, jsonRpcConfig.Timeout)
                .AddSingleton<IBlockchainBridgeFactory, ISimulateReadOnlyBlocksProcessingEnvFactory, IOverridableEnvFactory, ILifetimeScope>(
                    (simEnvFactory, overridableEnvFactory, lifetimeScope) =>
                        new BlockchainBridgeFactory(simEnvFactory, overridableEnvFactory, lifetimeScope,
                            jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount))
                .AddScoped<IBlockchainBridge>((ctx) => ctx.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge())
                    .AddSingleton<IFeeHistoryOracle, FeeHistoryOracle>()
                    .AddSingleton<IEthCapabilitiesProvider, EthCapabilitiesProvider>()
                    .AddSingleton<FilterStore, ITimerFactory, IJsonRpcConfig>((timerFactory, rpcConfig) => new FilterStore(timerFactory, rpcConfig.FiltersTimeout))
                    .AddSingleton<FilterManager>()
                    .AddSingleton<IWitnessGeneratingBlockProcessingEnvFactory, WitnessGeneratingBlockProcessingEnvFactory>()
                    .AddSingleton<ISimulateReadOnlyBlocksProcessingEnvFactory, SimulateReadOnlyBlocksProcessingEnvFactory>()

            // Proof
            .RegisterBoundedJsonRpcModule<IProofRpcModule, ProofModuleFactory>(2, jsonRpcConfig.Timeout)
                .AddScoped<IProofRpcModule, ProofRpcModule>()

            // Trace
            .AddSingleton<ParallelTraceBudget, IFlatDbConfig, ILogManager>(CreateTraceBudget)
            .AddSingleton<ParallelTraceBudgets, ISpecProvider, IFlatDbConfig, ParallelTraceBudget>(CreateParallelTraceBudgets)
            // Each instance holds two block-processing scopes for the life of the process. The lazy pool creates all
            // configured instances and its shared instance on first access, so lower the capacity where memory is short.
            .RegisterBoundedJsonRpcModule<ITraceRpcModule, TraceModuleFactory>(jsonRpcConfig.TraceModuleConcurrentInstances ?? Environment.ProcessorCount, jsonRpcConfig.Timeout)
                .AddScoped<ITraceRpcModule, TraceRpcModule>()

            // Debug
            .AddSingleton<IBatchConcurrencyStrategy, DebugTraceCallBatchConcurrency>()
            .RegisterBoundedJsonRpcModule<IDebugRpcModule, DebugModuleFactory>(jsonRpcConfig.GetDebugModuleConcurrentInstances(), jsonRpcConfig.Timeout)
                .AddScoped<GethStyleTracer.BlockProcessingComponents>()
                .AddScoped<IDebugBridge, DebugBridge>()
                .AddScoped<IDebugRpcModule, DebugRpcModule>()
                .AddScoped<IGethStyleTracer, GethStyleTracer>()

            ;
    }

    /// <summary>The one budget every parallel block trace on this node draws on, whichever seed the block takes. The
    /// deprecated FlatDb key keeps its published <c>int</c> shape, so its default <c>0</c> stands for "not set"; any other
    /// value an operator still sets sizes the budget and wins over <see cref="IJsonRpcConfig.TraceBlockParallelism"/>.</summary>
    private ParallelTraceBudget CreateTraceBudget(IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        int legacy = flatDbConfig.HistoryTransactionIndexTraceParallelism;
        if (legacy == 0)
            return ParallelTraceBudget.Bounded(jsonRpcConfig.TraceBlockParallelism);

        ILogger logger = logManager.GetClassLogger<RpcModules>();
        if (logger.IsWarn) logger.Warn($"FlatDb.{nameof(IFlatDbConfig.HistoryTransactionIndexTraceParallelism)} is deprecated, use JsonRpc.{nameof(IJsonRpcConfig.TraceBlockParallelism)} instead. Until it is removed, its value {legacy} sizes the parallel block trace budget in place of JsonRpc.{nameof(IJsonRpcConfig.TraceBlockParallelism)}.");
        return new ParallelTraceBudget(legacy);
    }

    /// <summary>Changeset seeds exist only where flat history captures the transaction index, the same switch that
    /// arms them; without it a block that carries no access list must not be offered to a parallel tracer that could
    /// never seed it.</summary>
    private static ParallelTraceBudgets CreateParallelTraceBudgets(ISpecProvider specProvider, IFlatDbConfig flatDbConfig, ParallelTraceBudget budget) =>
        new(specProvider, budget, flatDbConfig.Enabled && flatDbConfig.HistoryEnabled && flatDbConfig.HistoryTransactionIndexEnabled);

    private IAdminRpcModule CreateAdminRpcModule(IComponentContext ctx) => new AdminRpcModule(
            ctx.Resolve<IBlockTree>(),
            ctx.Resolve<INetworkConfig>(),
            ctx.Resolve<IPeerPool>(),
            ctx.Resolve<IStaticNodesManager>(),
            ctx.Resolve<IStateReader>(),
            ctx.Resolve<IEnode>(),
            ctx.Resolve<IInitConfig>().BaseDbPath, // IInitConfig not accessible from IAdminRpcModule, so we construct it manually here
            ctx.Resolve<ChainSpec>().Parameters,
            ctx.Resolve<ITrustedNodesManager>(),
            ctx.Resolve<ISubscriptionManager>(),
            ctx.Resolve<IJsonRpcConfig>(),
            ctx.Resolve<IBlockProcessingPauseControl>(),
            ctx.ResolveOptional<INodeRecordProvider>());
}
