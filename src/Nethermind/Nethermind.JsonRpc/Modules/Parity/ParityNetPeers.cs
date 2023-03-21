// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityNetPeers
    {
        [JsonPropertyName("active")]
        [JsonPropertyOrder(0)]
        public int Active { get; set; }

        [JsonPropertyName("connected")]
        [JsonPropertyOrder(1)]
        public int Connected { get; set; }

        [JsonPropertyName("max")]
        [JsonPropertyOrder(2)]
        public int Max { get; set; }

        [JsonPropertyName("peers")]
        [JsonPropertyOrder(3)]
        public PeerInfo[] Peers { get; set; }

        public ParityNetPeers()
        {
        }
    }
}
