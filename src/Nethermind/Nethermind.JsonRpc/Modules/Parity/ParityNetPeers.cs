// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityNetPeers
    {
        [JsonPropertyName("active")]
        public int Active { get; set; }

        [JsonPropertyName("connected")]
        public int Connected { get; set; }

        [JsonPropertyName("max")]
        public int Max { get; set; }

        [JsonPropertyName("peers")]
        public PeerInfo[] Peers { get; set; }

        public ParityNetPeers()
        {
        }
    }
}
