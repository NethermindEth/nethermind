// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

using System.Text.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    public class BuiltInJson
    {
        public string Name { get; set; }
        public Dictionary<string, JsonElement> Pricing { get; set; }
    }
}
