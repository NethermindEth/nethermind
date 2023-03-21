// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class EthProtocolInfo
    {
        [JsonPropertyName("difficulty")]
        [JsonPropertyOrder(0)]
        public UInt256 Difficulty { get; set; }
        [JsonPropertyName("genesis")]
        [JsonPropertyOrder(1)]
        public Keccak GenesisHash { get; set; }
        [JsonPropertyName("head")]
        [JsonPropertyOrder(2)]
        public Keccak HeadHash { get; set; }
        [JsonPropertyName("network")]
        [JsonPropertyOrder(3)]
        public ulong ChainId { get; set; }
    }
}
