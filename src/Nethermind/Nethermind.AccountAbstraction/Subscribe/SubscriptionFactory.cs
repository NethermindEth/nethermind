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
using Nethermind.AccountAbstraction.Source;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Subscribe
{
    public class SubscriptionFactory : ISubscriptionFactory
    {
        private readonly ILogManager _logManager;
        private readonly IUserOperationPool _userOperationPool;

        public SubscriptionFactory(ILogManager? logManager, IUserOperationPool userOperationPool)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _userOperationPool = userOperationPool ?? throw new ArgumentNullException(nameof(userOperationPool));
        }
        public Subscription CreateSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, SubscriptionType subscriptionType,
            Filter? filter)
        {
            switch (subscriptionType)
            {
                case SubscriptionType.NewPendingUserOps: 
                    return new NewPendingUserOpsSubscription(jsonRpcDuplexClient, _userOperationPool, _logManager);
                case SubscriptionType.NewReceivedUserOps:
                    throw new NotImplementedException();
                default: throw new Exception("Unexpected SubscriptionType.");
            }
        }
    }
}
