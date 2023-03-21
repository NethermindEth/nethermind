// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class EthProtocolInfo
    {
        [JsonPropertyName("version")]
        [JsonPropertyOrder(0)]
        public byte Version { get; set; }

        [JsonPropertyName("difficulty")]
        [JsonPropertyOrder(1)]
        public UInt256 Difficulty { get; set; }

        [JsonPropertyName("head")]
        [JsonPropertyOrder(2)]
        public Keccak HeadHash { get; set; }
    }
}
