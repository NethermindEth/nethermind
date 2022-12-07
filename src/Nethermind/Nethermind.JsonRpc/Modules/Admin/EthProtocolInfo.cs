// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class EthProtocolInfo
    {
        [JsonProperty("difficulty", Order = 0)]
        public UInt256 Difficulty { get; set; }
        [JsonProperty("genesis", Order = 1)]
        public Keccak GenesisHash { get; set; }
        [JsonProperty("head", Order = 2)]
        public Keccak HeadHash { get; set; }
        [JsonProperty("network", Order = 3)]
        public ulong ChainId { get; set; }
    }
}
