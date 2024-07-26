// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxTraceEntry
{
    public GethTxTraceEntry()
    {
    }

    [JsonPropertyName("pc")]
    [JsonConverter(typeof(LongRawJsonConverter))]
    public long ProgramCounter { get; set; }

    [JsonPropertyName("op")]
    public string? Opcode { get; set; }

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long Gas { get; set; }

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long GasCost { get; set; }

    public int Depth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Error { get; set; }

    public string[]? Stack { get; set; }

    public string[]? Memory { get; set; }

    public Dictionary<string, string>? Storage { get; set; }

    internal virtual void UpdateMemorySize(ulong size) { }
}
