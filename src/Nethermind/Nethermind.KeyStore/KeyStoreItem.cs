// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.KeyStore
{
    public class KeyStoreItem
    {
        [JsonProperty(PropertyName = "version", Order = 0)]
        public int Version { get; set; }

        [JsonProperty(PropertyName = "id", Order = 1)]
        public string Id { get; set; }

        [JsonProperty(PropertyName = "address", Order = 2)]
        public string Address { get; set; }

        [JsonProperty(PropertyName = "crypto", Order = 3)]
        public Crypto Crypto { get; set; }
    }
}
