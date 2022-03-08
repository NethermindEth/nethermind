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
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.AccountAbstraction.Subscribe;
public class NewPendingUserOpsSubscription : Subscription
{
    private readonly IUserOperationPool _userOperationPool;
    
    public NewPendingUserOpsSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IUserOperationPool? userOperationPool, ILogManager? logManager) 
        : base(jsonRpcDuplexClient)
    {
        _userOperationPool = userOperationPool ?? throw new ArgumentNullException(nameof(userOperationPool));
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            
        _userOperationPool.NewPending += OnNewPending;
        if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will track newPendingUserOperations");
    }
    
    private void OnNewPending(object? sender, UserOperationEventArgs e)
    {
        ScheduleAction(() =>
        {
            JsonRpcResult result = CreateSubscriptionMessage(new UserOperationRpc(e.UserOperation));
            JsonRpcDuplexClient.SendJsonRpcResult(result);
            if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} printed hash of newPendingUserOperations.");
        });
    }

    public override string Type => "newPendingUserOperations";

    public override void Dispose()
    {
        _userOperationPool.NewPending -= OnNewPending;
        base.Dispose();
        if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will no longer track newPendingUserOperations");
    }

}


