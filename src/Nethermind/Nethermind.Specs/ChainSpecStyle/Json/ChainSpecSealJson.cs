// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    public class ChainSpecSealJson
    {
        public ChainSpecEthereumSealJson Ethereum { get; set; }

        /// <summary>Engine-specific seal sections (e.g. <c>authorityRound</c>), parsed by the owning consensus plugin.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> CustomSeal { get; set; }
    }
}
