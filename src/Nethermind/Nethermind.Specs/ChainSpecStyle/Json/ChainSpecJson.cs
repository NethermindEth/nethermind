// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class ChainSpecJson
    {
        public string Name { get; set; }
        public string DataDir { get; set; }
        public EngineJson Engine { get; set; }
        public ChainSpecParamsJson Params { get; set; }
        [JsonPropertyName("genesis")]
        public ChainSpecGenesisJson Genesis { get; set; }
        public string[] Nodes { get; set; }
        [JsonPropertyName("accounts")]
        public Dictionary<string, AllocationJson> Accounts { get; set; }
        public Dictionary<string, byte[]>? CodeHashes { get; set; }

        public class EngineJson
        {
            [JsonExtensionData]
            public Dictionary<string, JsonElement> CustomEngineData { get; set; }
        }
    }
}
