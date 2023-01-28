// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerNetworkInfo
    {
        [JsonProperty("localAddress", Order = 0)]
        public string LocalAddress { get; set; }

        [JsonProperty("remoteAddress", Order = 1)]
        public string RemoteAddress { get; set; }
    }
}
