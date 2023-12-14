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
        public UInt256 Difficulty { get; set; }
        [JsonPropertyName("genesis")]
        public Hash256 GenesisHash { get; set; }
        [JsonPropertyName("head")]
        public Hash256 HeadHash { get; set; }
        [JsonPropertyName("network")]
        public ulong NewtorkId { get; set; }
    }
}
