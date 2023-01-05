// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Subscribe
{
    public class NewReceivedUserOpsSubscription : Subscription
    {
        private readonly IUserOperationPool[] _userOperationPoolsToTrack;
        private readonly bool _includeUserOperations;

        public NewReceivedUserOpsSubscription(
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
                pool.NewReceived += OnNewReceived;
            }

            if (_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} will track newReceivedUserOperations");
        }

        private void OnNewReceived(object? sender, UserOperationEventArgs e)
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
                if (_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} printed hash of newReceivedUserOperations.");
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
            if (_logger.IsTrace) _logger.Trace($"newReceivedUserOperations subscription {Id} will no longer track newReceivedUserOperations");
        }
    }
}


