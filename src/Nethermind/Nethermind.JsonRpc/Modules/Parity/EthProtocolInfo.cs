// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class EthProtocolInfo
    {
        [JsonProperty("version", Order = 0)]
        public byte Version { get; set; }

        [JsonProperty("difficulty", Order = 1)]
        public UInt256 Difficulty { get; set; }

        [JsonProperty("head", Order = 2)]
        public Keccak HeadHash { get; set; }
    }
}
