// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public interface ISubscriptionManager
    {
        string AddSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionType, string? args = null);
        bool RemoveSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, string subscriptionId);
        void RemoveClientSubscriptions(IJsonRpcDuplexClient jsonRpcDuplexClient);
    }
}
