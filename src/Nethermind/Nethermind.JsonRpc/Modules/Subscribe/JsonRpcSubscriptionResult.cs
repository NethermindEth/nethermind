// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public class JsonRpcSubscriptionResult
    {
        [JsonProperty(PropertyName = "result", Order = 1)]
        public object Result { get; set; }

        [JsonProperty(PropertyName = "subscription", Order = 0)]
        public string Subscription { get; set; }
    }
}
