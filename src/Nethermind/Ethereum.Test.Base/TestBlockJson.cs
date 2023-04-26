// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Ethereum.Test.Base
{
    public class TestBlockJson
    {
        public TestBlockHeaderJson? BlockHeader { get; set; }
        public TestBlockHeaderJson[]? UncleHeaders { get; set; }
        public string? Rlp { get; set; }
        public LegacyTransactionJson[]? Transactions { get; set; }
        [JsonProperty("expectException")]
        private string? ExpectException { set { ExpectedException = value; } }
        [JsonProperty("expectExceptionALL")]
        public string? ExpectedException { get; set; }
    }
}
