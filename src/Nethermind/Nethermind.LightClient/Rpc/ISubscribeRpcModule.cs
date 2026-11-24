// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

[RpcModule(ModuleType.Subscribe)]
internal interface ISubscribeRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true, IsSharable = true, Availability = Nethermind.JsonRpc.Modules.RpcEndpoint.Ws)]
    ResultWrapper<string> eth_subscribe(string subscriptionName, JsonElement? filter = null);

    [JsonRpcMethod(IsImplemented = true, IsSharable = true, Availability = Nethermind.JsonRpc.Modules.RpcEndpoint.Ws)]
    ResultWrapper<bool> eth_unsubscribe(string subscriptionId);
}
