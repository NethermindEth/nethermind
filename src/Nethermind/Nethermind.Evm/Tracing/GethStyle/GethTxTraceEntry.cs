// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethTxTraceEntry
{
    public GethTxTraceEntry()
    {
    }

    public int Depth { get; set; }

    public string? Error { get; set; }

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long Gas { get; set; }

    public long GasCost { get; set; }

    public List<string>? Memory { get; set; }

    [JsonPropertyName("op")]
    public string? Opcode { get; set; }

    [JsonPropertyName("pc")]
    [JsonConverter(typeof(LongRawJsonConverter))]
    public long ProgramCounter { get; set; }


    public List<string>? Stack { get; set; }


    public Dictionary<string, string>? Storage { get; set; }

    internal virtual void UpdateMemorySize(ulong size) { }
}

public class LongRawJsonConverter : JsonConverter<long>
{
    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => throw new NotImplementedException();

    public override void Write(
        Utf8JsonWriter writer,
        long value,
        JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
