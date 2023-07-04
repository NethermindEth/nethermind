// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.KeyStore
{
    public class Crypto
    {
        [JsonProperty(PropertyName = "ciphertext", Order = 0)]
        public string CipherText { get; set; }

        [JsonProperty(PropertyName = "cipherparams", Order = 1)]
        public CipherParams CipherParams { get; set; }

        [JsonProperty(PropertyName = "cipher", Order = 2)]
        public string Cipher { get; set; }

        [JsonProperty(PropertyName = "kdf", Order = 3)]
        public string KDF { get; set; }

        [JsonProperty(PropertyName = "kdfparams", Order = 4)]
        public KdfParams KDFParams { get; set; }

        [JsonProperty(PropertyName = "mac", Order = 5)]
        public string MAC { get; set; }
    }
}
