// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public interface ISubscriptionFactory
    {
        Subscription CreateSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType, string? args = null);

        void RegisterSubscriptionType<TParam>(string subscriptionType, Func<IJsonRpcDuplexClient, TParam, Subscription> customSubscriptionDelegate)
            where TParam : IJsonRpcParam?, new();

        void RegisterSubscriptionType(string subscriptionType, Func<IJsonRpcDuplexClient, Subscription> customSubscriptionDelegate);
    }
}
