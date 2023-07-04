// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    [RpcModule(ModuleType.Subscribe)]
    public interface ISubscribeRpcModule : IContextAwareRpcModule
    {
        [JsonRpcMethod(Description = "Starts a subscription (on WebSockets/Sockets) to a particular event. For every event that matches the subscription a JSON-RPC notification with event details and subscription ID will be sent to a client.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
        ResultWrapper<string> eth_subscribe(string subscriptionName, string? args = null);

        [JsonRpcMethod(Description = "Unsubscribes from a subscription.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
        ResultWrapper<bool> eth_unsubscribe(string subscriptionId);
    }
}
