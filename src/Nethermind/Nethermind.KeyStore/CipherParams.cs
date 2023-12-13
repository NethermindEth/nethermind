// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class CipherParams
    {
        [JsonPropertyName("iv")]
        public string IV { get; set; }
    }
}
