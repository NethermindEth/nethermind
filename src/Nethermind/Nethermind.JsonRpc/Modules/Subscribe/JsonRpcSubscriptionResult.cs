// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Facade.Eth;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    [JsonSerializable(typeof(SyncingResult))]
    public class JsonRpcSubscriptionResult
    {
        [JsonPropertyOrder(0)]
        [JsonPropertyName("subscription")]
        public string Subscription { get; set; }

        [JsonPropertyOrder(1)]
        [JsonPropertyName("result")]
        public object Result { get; set; }
    }
}
