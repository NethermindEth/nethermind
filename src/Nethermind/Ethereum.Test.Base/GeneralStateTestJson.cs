// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ethereum.Test.Base
{
    public class GeneralStateTestJson
    {
        [JsonPropertyName("_info")]
        public GeneralStateTestInfoJson? Info { get; set; }
        public GeneralStateTestEnvJson? Env { get; set; }
        public Dictionary<string, PostStateJson[]>? Post { get; set; }
        public Dictionary<string, AccountStateJson>? Pre { get; set; }
        public string? SealEngine { get; set; }
        public string? LoadFailure { get; set; }
        public TransactionJson? Transaction { get; set; }
    }
}
