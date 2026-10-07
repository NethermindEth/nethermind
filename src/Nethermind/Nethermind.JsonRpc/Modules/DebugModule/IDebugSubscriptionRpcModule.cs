// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.DebugModule;

[RpcModule(ModuleType.Debug)]
internal interface IDebugSubscriptionRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true, IsSharable = true, Availability = RpcEndpoint.Ws | RpcEndpoint.IPC)]
    ResultWrapper<string> debug_subscribe(string subscription, BlockParameter start, BlockParameter end, TraceChainOptions? options = null);

    [JsonRpcMethod(IsImplemented = true, IsSharable = true, Availability = RpcEndpoint.Ws | RpcEndpoint.IPC)]
    ResultWrapper<bool> debug_unsubscribe(string subscriptionId);
}
