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
using System.Linq;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Subscribe
{
    public class NewPendingUserOpsSubscription : Subscription
    {
        private readonly IUserOperationPool[] _userOperationPoolsToTrack;
        private readonly bool _includeUserOperations;
    
        public NewPendingUserOpsSubscription(
            IJsonRpcDuplexClient jsonRpcDuplexClient, 
            IDictionary<Address, IUserOperationPool>? userOperationPools, 
            ILogManager? logManager, 
            UserOperationSubscriptionParam? userOperationSubscriptionParam = null) 
            : base(jsonRpcDuplexClient)
        {
            if (userOperationPools is null) throw new ArgumentNullException(nameof(userOperationPools));
            if (userOperationSubscriptionParam is not null)
            {
                if (userOperationSubscriptionParam.EntryPoints.Length == 0)
                {
                    _userOperationPoolsToTrack = userOperationPools.Values.ToArray();
                }
                else
                {
                    _userOperationPoolsToTrack = userOperationPools
                        .Where(kv => userOperationSubscriptionParam.EntryPoints.Contains(kv.Key))
                        .Select(kv => kv.Value)
                        .ToArray();
                }

                _includeUserOperations = userOperationSubscriptionParam.IncludeUserOperations;
            }
            else
            {
                // use all pools
                _userOperationPoolsToTrack = userOperationPools.Values.ToArray();
            }
        
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            foreach (var pool in _userOperationPoolsToTrack)
            {
                pool.NewPending += OnNewPending;
            }
        
            if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will track newPendingUserOperations");
        }
    
        private void OnNewPending(object? sender, UserOperationEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result;
                if (_includeUserOperations)
                {
                    result = CreateSubscriptionMessage(new { UserOperation = new UserOperationRpc(e.UserOperation), EntryPoint = e.EntryPoint });
                }
                else
                {
                    result = CreateSubscriptionMessage(new { UserOperation = e.UserOperation.RequestId, EntryPoint = e.EntryPoint });
                }
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} printed hash of newPendingUserOperations.");
            });
        }

        public override string Type => "newPendingUserOperations";

        public override void Dispose()
        {
            foreach (var pool in _userOperationPoolsToTrack)
            {
                pool.NewPending -= OnNewPending;
            }
            base.Dispose();
            if(_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will no longer track newPendingUserOperations");
        }
    }
}


