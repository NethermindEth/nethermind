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
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Api;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Subscribe;

//NOTE: The methods in this factory could be integrated into Nethermind.JsonRpc.Modules.Subscribe.SubscriptionFactory.
//A separate factory was created (with its same interface) so that all EIP-4337 capabilities function as a plugin to
//the client's core features.

public class UserOpSubscriptionFactory : ISubscriptionFactory
{
    private readonly ILogManager _logManager;
    private readonly IUserOperationPool _userOperationPool;
    private static Dictionary<string, Delegate>? _customSubscriptions;
    //TODO: make this more specific in terms of allowed delegates?

    public string Type => "newPendingUserOperations";

    public UserOpSubscriptionFactory(ILogManager? logManager, IUserOperationPool userOperationPool)
    {
        _customSubscriptions = new Dictionary<string, Delegate>();
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _userOperationPool = userOperationPool ?? throw new ArgumentNullException(nameof(userOperationPool));
    }
    public Subscription CreateSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType,
        Filter? filter)
    {
        return (subscriptionType == Type
            ? new NewPendingUserOpsSubscription(jsonRpcDuplexClient, _userOperationPool, _logManager)
            : throw new Exception("Unexpected UserOperation SubscriptionType."));
    }
    private delegate Subscription CustomSubscriptionDelegate(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType, Filter? filter);

    private void RegisterCustomSubscription()
    {
        if (!_customSubscriptions!.ContainsKey(Type))
        {
            CustomSubscriptionDelegate newPendingUserOperationsDelegate = CreateSubscription;
            _customSubscriptions.Add(Type, newPendingUserOperationsDelegate);
        }
    }
}
