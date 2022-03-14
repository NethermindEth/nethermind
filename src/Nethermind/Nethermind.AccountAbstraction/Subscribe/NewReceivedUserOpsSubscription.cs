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
using System.IO;
using System.Linq;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.AccountAbstraction.Subscribe
{
    public class NewReceivedUserOpsSubscription : Subscription
    {
        private readonly IUserOperationPool[] _userOperationPoolsToTrack;
    
        public NewReceivedUserOpsSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, IDictionary<Address, IUserOperationPool>? userOperationPools, ILogManager? logManager, EntryPointsParam? entryPoints = null) 
            : base(jsonRpcDuplexClient)
        {
            if (userOperationPools is null) throw new ArgumentNullException(nameof(userOperationPools));
            if (entryPoints is not null)
            {
                Address[] addressFilter = DecodeAddresses(entryPoints.EntryPoints);
                _userOperationPoolsToTrack = userOperationPools
                    .Where(kv => addressFilter.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .ToArray();
            }
            else
            {
                // use all pools
                _userOperationPoolsToTrack = userOperationPools.Values.ToArray();
            }
        
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            foreach (var pool in _userOperationPoolsToTrack)
            {
                pool.NewReceived += OnNewReceived;
            }
        
            if(_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} will track newReceivedUserOperations");
        }
    
        private void OnNewReceived(object? sender, UserOperationEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result = CreateSubscriptionMessage(new { UserOperation = new UserOperationRpc(e.UserOperation), EntryPoint = e.EntryPoint });
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if(_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} printed hash of newReceivedUserOperations.");
            });
        }

        public override string Type => "newReceivedUserOperations";

        public override void Dispose()
        {
            foreach (var pool in _userOperationPoolsToTrack)
            {
                pool.NewReceived -= OnNewReceived;
            }
            base.Dispose();
            if(_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} will no longer track newReceivedUserOperations");
        }

        private static Address[] DecodeAddresses(object? entryPoints)
        {
            if (entryPoints is null)
            {
                throw new InvalidDataException("No entryPoint addresses to decode");
            }

            if (entryPoints is string s)
            {
                return new Address[] {new(s)};
            }
            
            if (entryPoints is IEnumerable<string> e)
            {
                return e.Select(a => new Address(a)).ToArray();
            }
            
            throw new InvalidDataException("Invalid address filter format");
        }

    }
}


