// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

using Nethermind.Core;

namespace Ethereum.Test.Base
{
    public class AccessListItemJson
    {
        [JsonPropertyName("address")]
        public Address Address { get; set; }

        [JsonPropertyName("storageKeys")]
        public byte[][] StorageKeys { get; set; }
    }
}
