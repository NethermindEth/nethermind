//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    [RpcModule(ModuleType.Subscribe)]
    public interface ISubscribeRpcModule : IContextAwareRpcModule
    {
        [JsonRpcMethod(Description = "Starts a subscription (on WebSockets/Sockets) to a particular event. For every event that matches the subscription a JSON-RPC notification with event details and subscription ID will be sent to a client.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
        ResultWrapper<string> eth_subscribe(string subscriptionName, Filter arguments = null);
        
        [JsonRpcMethod(Description = "Unsubscribes from a subscription.", IsImplemented = true, IsSharable = false, Availability = RpcEndpoint.All & ~RpcEndpoint.Http)]
        ResultWrapper<bool> eth_unsubscribe(string subscriptionId);
    }
}
