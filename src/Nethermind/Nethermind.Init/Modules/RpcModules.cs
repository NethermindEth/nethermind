// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.JsonRpc.Modules.Web3;

namespace Nethermind.Init.Modules;

public class RpcModules : Module
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
            ;
    }
}
