// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Newtonsoft.Json;

namespace Ethereum.Test.Base
{
    public class AccessListItemJson
    {
        [JsonProperty("address")]
        public Address Address { get; set; }

        [JsonProperty("storageKeys")]
        public byte[][] StorageKeys { get; set; }
    }
}
