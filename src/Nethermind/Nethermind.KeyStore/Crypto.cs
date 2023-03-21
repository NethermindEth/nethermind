// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class Crypto
    {
        [JsonPropertyName("ciphertext")]
        [JsonPropertyOrder(0)]
        public string CipherText { get; set; }

        [JsonPropertyName("cipherparams")]
        [JsonPropertyOrder(1)]
        public CipherParams CipherParams { get; set; }

        [JsonPropertyName("cipher")]
        [JsonPropertyOrder(2)]
        public string Cipher { get; set; }

        [JsonPropertyName("kdf")]
        [JsonPropertyOrder(3)]
        public string KDF { get; set; }

        [JsonPropertyName("kdfparams")]
        [JsonPropertyOrder(4)]
        public KdfParams KDFParams { get; set; }

        [JsonPropertyName("mac")]
        [JsonPropertyOrder(5)]
        public string MAC { get; set; }
    }
}
