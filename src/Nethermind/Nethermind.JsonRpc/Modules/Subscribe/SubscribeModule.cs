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
    public class SubscribeModule : ISubscribeModule
    {
        private readonly ISubscriptionManger _subscriptionManger;

        public SubscribeModule(ISubscriptionManger subscriptionManger)
        {
            _subscriptionManger = subscriptionManger ?? throw new ArgumentNullException(nameof(subscriptionManger));
        }
        
        
        public ResultWrapper<string> eth_subscribe(string subscriptionName, Filter arguments = null)
        {
            if (Enum.TryParse(typeof(SubscriptionType), subscriptionName, true, out var subscriptionType))
            {
                return ResultWrapper<string>.Success(_subscriptionManger.AddSubscription((SubscriptionType)subscriptionType, arguments));
            }
            return ResultWrapper<string>.Fail("Wrong subscription type.");
        }
        

        public ResultWrapper<bool> eth_unsubscribe(string subscriptionId)
        {
            bool unsubscribed = _subscriptionManger.RemoveSubscription(subscriptionId);
            return unsubscribed
                ? ResultWrapper<bool>.Success(true)
                : ResultWrapper<bool>.Fail("Failed to unsubscribe.");
        }
    }
}
