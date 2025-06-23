// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Simulate;
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
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class RpcModules(IJsonRpcConfig jsonRpcConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IEthSyncingInfo, EthSyncingInfo>()
            .AddSingleton<IRpcModuleProvider, RpcModuleProvider>()
            .AddSingleton<IJsonRpcLocalStats, JsonRpcLocalStats>()

            // Smallish RPCs
            .AddSingleton<INetBridge, NetBridge>()
            .RegisterSingletonJsonRpcModule<INetRpcModule, NetRpcModule>()
            .RegisterSingletonJsonRpcModule<IParityRpcModule, ParityRpcModule>()
            .RegisterSingletonJsonRpcModule<IWeb3RpcModule, Web3RpcModule>()
            .RegisterSingletonJsonRpcModule<IPersonalRpcModule, PersonalRpcModule>()
            .RegisterSingletonJsonRpcModule<IRpcRpcModule, RpcRpcModule>()

            // Txpool rpc
            .AddSingleton<ITxPoolInfoProvider, TxPoolInfoProvider>()
            .RegisterSingletonJsonRpcModule<ITxPoolRpcModule, TxPoolRpcModule>()

            // Subscriptions
            .AddSingleton<IReceiptMonitor, ReceiptCanonicalityMonitor>()
            .AddSingleton<ISubscriptionFactory, SubscriptionFactory>()
            .AddSingleton<ISubscriptionManager, SubscriptionManager>()
            .RegisterSingletonJsonRpcModule<ISubscribeRpcModule, SubscribeRpcModule>()

            // Admin
            .AddSingleton<IAdminRpcModule>(CreateAdminRpcModule)
            .RegisterSingletonJsonRpcModule<IAdminRpcModule>()

            // Eth
            .AddSingleton<SimulateReadOnlyBlocksProcessingEnvFactory>()
            .AddSingleton<IBlockchainBridgeFactory, BlockchainBridgeFactory>()
            .AddScoped<IBlockchainBridge>((ctx) => ctx.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge())
            .AddSingleton<IFeeHistoryOracle, FeeHistoryOracle>()
            .RegisterBoundedJsonRpcModule<IEthRpcModule, EthModuleFactory>(jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, jsonRpcConfig.Timeout)

            // Proof
            .AddScoped<IProofRpcModule, ProofRpcModule>()
            .RegisterBoundedJsonRpcModule<IProofRpcModule, ProofModuleFactory>(2, jsonRpcConfig.Timeout)

            // Trace
            .AddScoped<ITraceRpcModule, TraceRpcModule>()
            .RegisterBoundedJsonRpcModule<ITraceRpcModule, TraceModuleFactory>(2, jsonRpcConfig.Timeout)

            // Debug
            .AddScoped<IGethStyleTracer, GethStyleTracer>()
            .AddScoped<IReceiptsMigration, ReceiptMigration>()
            .AddScoped<IDebugBridge, DebugBridge>()
            .AddScoped<IDebugRpcModule, DebugRpcModule>()
            .RegisterBoundedJsonRpcModule<IDebugRpcModule, DebugModuleFactory>(Environment.ProcessorCount, jsonRpcConfig.Timeout)
            ;
    }

    private IAdminRpcModule CreateAdminRpcModule(IComponentContext ctx)
    {
        return new AdminRpcModule (
            ctx.Resolve<IBlockTree>(),
            ctx.Resolve<INetworkConfig>(),
            ctx.Resolve<IPeerPool>(),
            ctx.Resolve<IStaticNodesManager>(),
            ctx.Resolve<IStateReader>(),
            ctx.Resolve<IEnode>(),
            ctx.Resolve<IInitConfig>().BaseDbPath, // IInitConfig not accessible from IAdminRpcModule, so we construct it manually here
            ctx.Resolve<ChainSpec>().Parameters,
            ctx.Resolve<ITrustedNodesManager>(),
            ctx.Resolve<ISubscriptionManager>());
    }
}
