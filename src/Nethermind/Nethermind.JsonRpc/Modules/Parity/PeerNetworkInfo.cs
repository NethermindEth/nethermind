// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerNetworkInfo
    {
        [JsonPropertyName("localAddress")]
        public string LocalAddress { get; set; }

        [JsonPropertyName("remoteAddress")]
        public string RemoteAddress { get; set; }
    }
}
