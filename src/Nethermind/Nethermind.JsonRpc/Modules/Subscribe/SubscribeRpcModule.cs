// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            catch (KeyNotFoundException)
            {
                return ResultWrapper<string>.Fail($"Wrong subscription type: {subscriptionName}.");
            }
            catch (ArgumentException e)
            {
                return ResultWrapper<string>.Fail($"Invalid params", ErrorCodes.InvalidParams, e.Message);
            }
            catch (JsonException)
            {
                return ResultWrapper<string>.Fail($"Invalid params", ErrorCodes.InvalidParams);
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
