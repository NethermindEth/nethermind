// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class Crypto
    {
        [JsonPropertyName("ciphertext")]
        public string CipherText { get; set; }

        [JsonPropertyName("cipherparams")]
        public CipherParams CipherParams { get; set; }

        [JsonPropertyName("cipher")]
        public string Cipher { get; set; }

        [JsonPropertyName("kdf")]
        public string KDF { get; set; }

        [JsonPropertyName("kdfparams")]
        public KdfParams KDFParams { get; set; }

        [JsonPropertyName("mac")]
        public string MAC { get; set; }
    }
}
