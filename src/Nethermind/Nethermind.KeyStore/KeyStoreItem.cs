// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class KeyStoreItem
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("address")]
        public string Address { get; set; }

        [JsonPropertyName("crypto")]
        public Crypto Crypto { get; set; }
    }
}
