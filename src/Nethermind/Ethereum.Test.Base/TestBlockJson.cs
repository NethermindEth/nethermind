// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson? BlockHeader { get; set; }
        public TestBlockHeaderJson[]? UncleHeaders { get; set; }
        public string? Rlp { get; set; }
        public LegacyTransactionJson[]? Transactions { get; set; }
        [JsonPropertyName("expectException")]
        public string? ExpectedException { get; set; }
    }
}
