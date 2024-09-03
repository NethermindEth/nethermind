// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ethereum.Test.Base
{
    public class EofTestJson
    {
        [JsonPropertyName("_info")]
        public GeneralStateTestInfoJson? Info { get; set; }

        public Dictionary<string, VectorTestJson> Vectors { get; set; }

    }
}
