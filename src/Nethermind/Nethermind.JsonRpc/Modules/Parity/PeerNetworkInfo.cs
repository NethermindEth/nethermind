// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerNetworkInfo
    {
        [JsonPropertyOrder(0)]
        [JsonPropertyName("localAddress")]
        public string LocalAddress { get; set; }

        [JsonPropertyOrder(1)]
        [JsonPropertyName("remoteAddress")]
        public string RemoteAddress { get; set; }
    }
}
