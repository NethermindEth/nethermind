// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class KeyStoreItem
    {
        [JsonPropertyName("version")]
        [JsonPropertyOrder(0)]
        public int Version { get; set; }

        [JsonPropertyName("id")]
        [JsonPropertyOrder(1)]
        public string Id { get; set; }

        [JsonPropertyName("address")]
        [JsonPropertyOrder(2)]
        public string Address { get; set; }

        [JsonPropertyName("crypto")]
        [JsonPropertyOrder(3)]
        public Crypto Crypto { get; set; }
    }
}
