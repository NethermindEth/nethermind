// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed class VerifiedSubscribeRpcModule : IVerifiedSubscribeRpcModule
{
    public ResultWrapper<string> eth_subscribe(string subscriptionName, JsonElement? filter = null) =>
        VerifiedSubscriptions.Subscribe(JsonRpcContext.Current?.DuplexClient, subscriptionName, filter);

    public ResultWrapper<bool> eth_unsubscribe(string subscriptionId) =>
        VerifiedSubscriptions.Unsubscribe(JsonRpcContext.Current?.DuplexClient, subscriptionId);
}
