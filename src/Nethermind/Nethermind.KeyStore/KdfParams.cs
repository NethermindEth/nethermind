// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class KdfParams
    {
        [JsonPropertyName("dklen")]
        public int DkLen { get; set; }

        [JsonPropertyName("salt")]
        public string Salt { get; set; }

        [JsonPropertyName("n")]
        public int? N { get; set; }

        [JsonPropertyName("r")]
        public int? R { get; set; }

        [JsonPropertyName("p")]
        public int? P { get; set; }

        [JsonPropertyName("c")]
        public int? C { get; set; }

        [JsonPropertyName("prf")]
        public string Prf { get; set; }
    }
}
