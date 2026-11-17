// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.JsonRpc.Modules.Admin.Models
{
    public class EthProtocolInfo
    {
        public UInt256 Difficulty { get; set; }
        [JsonPropertyName("genesis")]
        public Hash256 GenesisHash { get; set; }
        [JsonPropertyName("head")]
        public Hash256 HeadHash { get; set; }
        [JsonPropertyName("network")]
        public ulong NetworkId { get; set; }
        public ulong ChainId { get; set; }
        public ChainParameters Config { get; set; }
    }
}
