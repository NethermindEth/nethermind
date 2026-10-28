// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    public class ChainSpecEthereumSealJson
    {
        [JsonConverter(typeof(ULongConverter))]
        public ulong Nonce { get; set; }
        public Hash256? MixHash { get; set; }
    }
}
