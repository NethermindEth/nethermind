// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will track newPendingUserOperations");
        }

        private void OnNewPending(object? sender, UserOperationEventArgs e)
        {
            ScheduleAction(() =>
            {
                JsonRpcResult result;
                if (_includeUserOperations)
                {
                    result = CreateSubscriptionMessage(new { UserOperation = new UserOperationRpc(e.UserOperation), e.EntryPoint });
                }
                else
                {
                    result = CreateSubscriptionMessage(new { UserOperation = e.UserOperation.RequestId, e.EntryPoint });
                }
                JsonRpcDuplexClient.SendJsonRpcResult(result);
                if (_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} printed hash of newPendingUserOperations.");
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
            if (_logger.IsTrace) _logger.Trace($"newPendingUserOperations subscription {Id} will no longer track newPendingUserOperations");
        }
    }
}


