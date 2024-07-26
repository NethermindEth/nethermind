// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Ethereum.Abi.Test
{
    public class AbiTest
    {
        [JsonPropertyName("args")]
        public object[] Args { get; set; }

        [JsonPropertyName("result")]
        public string Result { get; set; }

        [JsonPropertyName("types")]
        public string[] Types { get; set; }
    }
}
