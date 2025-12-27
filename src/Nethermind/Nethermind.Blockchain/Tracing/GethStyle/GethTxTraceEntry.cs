// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

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
    public long gas { get; set; }

    [JsonIgnore]
    public long Gas
    {
        get => gas;
        set => gas = value;
    }

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
