// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public static class GethTxTraceEntryJsonWriter
{
    public static void Write(Utf8JsonWriter writer, GethTxTraceEntry entry)
    {
        writer.WriteStartObject();

        writer.WriteNumber("pc"u8, entry.ProgramCounter);

        if (entry.Opcode is not null) writer.WriteString("op"u8, entry.Opcode);

        writer.WriteNumber("gas"u8, entry.Gas);
        writer.WriteNumber("gasCost"u8, entry.GasCost);
        writer.WriteNumber("depth"u8, entry.Depth);

        if (entry.Error is null) writer.WriteNull("error"u8);
        else writer.WriteString("error"u8, entry.Error);

        if (entry.Stack is not null) WriteStringArray(writer, "stack"u8, entry.Stack);
        if (entry.Memory is not null) WriteStringArray(writer, "memory"u8, entry.Memory);
        if (entry.Storage is not null) WriteStorage(writer, entry.Storage);

        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, System.ReadOnlySpan<byte> name, string[] values)
    {
        writer.WriteStartArray(name);
        foreach (string? s in values)
            writer.WriteStringValue(s);

        writer.WriteEndArray();
    }

    private static void WriteStorage(Utf8JsonWriter writer, Dictionary<string, string> storage)
    {
        writer.WriteStartObject("storage"u8);
        foreach (KeyValuePair<string, string> kv in storage)
        {
            writer.WritePropertyName(kv.Key);
            writer.WriteStringValue(kv.Value);
        }
        writer.WriteEndObject();
    }
}
