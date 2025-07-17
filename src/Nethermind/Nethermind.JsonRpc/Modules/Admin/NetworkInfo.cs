// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class NetworkInfo
    {
        [JsonPropertyName("localAddress")]
        public string LocalAddress { get; set; } = string.Empty;

        [JsonPropertyName("remoteAddress")]
        public string RemoteAddress { get; set; } = string.Empty;

        [JsonPropertyName("inbound")]
        public bool Inbound { get; set; }

        [JsonPropertyName("trusted")]
        public bool Trusted { get; set; }

        [JsonPropertyName("static")]
        public bool Static { get; set; }
    }
}
