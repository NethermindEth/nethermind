// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.KeyStore
{
    public class KdfParams
    {
        [JsonProperty(PropertyName = "dklen", Order = 0)]
        public int DkLen { get; set; }

        [JsonProperty(PropertyName = "salt", Order = 1)]
        public string Salt { get; set; }

        [JsonProperty(PropertyName = "n", Order = 2)]
        public int? N { get; set; }

        [JsonProperty(PropertyName = "p", Order = 4)]
        public int? P { get; set; }

        [JsonProperty(PropertyName = "r", Order = 3)]
        public int? R { get; set; }

        [JsonProperty(PropertyName = "c")]
        public int? C { get; set; }

        [JsonProperty(PropertyName = "prf")]
        public string Prf { get; set; }
    }
}
