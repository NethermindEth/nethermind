// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;

namespace Nethermind.Init.Modules;

public class RpcModules(IJsonRpcConfig jsonRpcConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IEthSyncingInfo, EthSyncingInfo>()
            .AddSingleton<IRpcModuleProvider, RpcModuleProvider>()

            .RegisterSingletonJsonRpcModule<ITxPoolRpcModule, TxPoolRpcModule>()
            .AddSingleton<INetBridge, NetBridge>()
            .RegisterSingletonJsonRpcModule<INetRpcModule, NetRpcModule>()
            .RegisterSingletonJsonRpcModule<IParityRpcModule, ParityRpcModule>()
            .RegisterSingletonJsonRpcModule<IWeb3RpcModule, Web3RpcModule>()

            .AddSingleton<IFeeHistoryOracle, FeeHistoryOracle>()
            .RegisterBoundedJsonRpcModule<IEthRpcModule, EthModuleFactory>(jsonRpcConfig.EthModuleConcurrentInstances ?? Environment.ProcessorCount, jsonRpcConfig.Timeout)

            .AddScoped<IProofRpcModule, ProofRpcModule>()
            .RegisterBoundedJsonRpcModule<IProofRpcModule, ProofModuleFactory>(2, jsonRpcConfig.Timeout)

            .AddScoped<ITraceRpcModule, TraceRpcModule>()
            .RegisterBoundedJsonRpcModule<ITraceRpcModule, TraceModuleFactory>(2, jsonRpcConfig.Timeout)

            .AddScoped<IGethStyleTracer, GethStyleTracer>()
            .AddScoped<IReceiptsMigration, ReceiptMigration>()
            .AddScoped<IDebugBridge, DebugBridge>()
            .AddScoped<IDebugRpcModule, DebugRpcModule>()
            .RegisterBoundedJsonRpcModule<IDebugRpcModule, DebugModuleFactory>(Environment.ProcessorCount, jsonRpcConfig.Timeout)
            ;
    }
}
