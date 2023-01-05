// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityNetPeers
    {
        [JsonProperty("active", Order = 0)]
        public int Active { get; set; }

        [JsonProperty("connected", Order = 1)]
        public int Connected { get; set; }

        [JsonProperty("max", Order = 2)]
        public int Max { get; set; }

        [JsonProperty("peers", Order = 3)]
        public PeerInfo[] Peers { get; set; }

        public ParityNetPeers()
        {
        }
    }
}
