// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class KdfParams
    {
        [JsonPropertyName("dklen")]
        [JsonPropertyOrder(0)]
        public int DkLen { get; set; }

        [JsonPropertyName("salt")]
        [JsonPropertyOrder(1)]
        public string Salt { get; set; }

        [JsonPropertyName("n")]
        [JsonPropertyOrder(2)]
        public int? N { get; set; }

        [JsonPropertyName("r")]
        [JsonPropertyOrder(3)]
        public int? R { get; set; }

        [JsonPropertyName("p")]
        [JsonPropertyOrder(4)]
        public int? P { get; set; }

        [JsonPropertyName("c")]
        public int? C { get; set; }

        [JsonPropertyName("prf")]
        public string Prf { get; set; }
    }
}
