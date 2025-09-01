// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nethermind.Consensus.HotStuff.Types;
public class ExpTimeoutConfig
{
    [JsonPropertyName("base")]
    public int Base { get; set; }

    [JsonPropertyName("maxExponent")]
    public byte MaxExponent { get; set; }
}
