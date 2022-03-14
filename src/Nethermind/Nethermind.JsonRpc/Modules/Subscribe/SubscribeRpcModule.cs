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

using System;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class SubscribeRpcModule : ISubscribeRpcModule
    {
        private readonly ISubscriptionManager _subscriptionManager;

        public SubscribeRpcModule(ISubscriptionManager subscriptionManager)
        {
            _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));
        }

        public ResultWrapper<string> eth_subscribe(string subscriptionName, string? args = null)
        {
            try
            {
                ResultWrapper<string> successfulResult = ResultWrapper<string>.Success(_subscriptionManager.AddSubscription(Context.DuplexClient, subscriptionName, args));
                return successfulResult;
            }
            catch (ArgumentException e)
            {
                return ResultWrapper<string>.Fail($"Wrong subscription type: {subscriptionName}.");
            }
        }

        public ResultWrapper<bool> eth_unsubscribe(string subscriptionId)
        {
            bool unsubscribed = _subscriptionManager.RemoveSubscription(Context.DuplexClient, subscriptionId);
            return unsubscribed
                ? ResultWrapper<bool>.Success(true)
                : ResultWrapper<bool>.Fail($"Failed to unsubscribe: {subscriptionId}.");
        }

        public JsonRpcContext Context { get; set; }
        
        
    }
}
