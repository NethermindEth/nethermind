// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class BuiltInJson
    {
        public string Name { get; set; }
        public Dictionary<string, JObject> Pricing { get; set; }
    }
}
