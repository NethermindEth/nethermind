// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.KeyStore
{
    public class CipherParams
    {
        [JsonProperty(PropertyName = "iv")]
        public string IV { get; set; }
    }
}
