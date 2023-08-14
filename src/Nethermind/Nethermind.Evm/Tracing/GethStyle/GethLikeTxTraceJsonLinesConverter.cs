// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

/// <summary>
/// Converts a transaction trace entry to the
/// <see href="https://jsonlines.org">JSON Lines</see> format.
/// This converter is write-only.
/// </summary>
internal class GethLikeTxTraceJsonLinesConverter : JsonConverter<GethTxFileTraceEntry>
{
    /// <summary>
    /// This method is not supported.
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public override GethTxFileTraceEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    /// <summary>
    /// Writes specified transaction entry as JSON adding a new line at the end.
    /// </summary>
    /// <remarks>
    /// This method flushes and resets the writer before returning.
    /// </remarks>
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, GethTxFileTraceEntry value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WritePropertyName("pc");
        writer.WriteNumberValue(value.ProgramCounter);

        writer.WritePropertyName("op");
        writer.WriteNumberValue((byte)value.OpcodeRaw);

        writer.WritePropertyName("gas");
        writer.WriteStringValue($"0x{value.Gas:x}");

        writer.WritePropertyName("gasCost");
        writer.WriteStringValue($"0x{value.GasCost:x}");

        writer.WritePropertyName("memSize");
        writer.WriteNumberValue(value.MemorySize ?? 0UL);

        if ((value.Memory?.Count ?? 0) != 0)
        {
            var memory = string.Concat(value.Memory);

            writer.WritePropertyName("memory");
            writer.WriteStringValue($"0x{memory}");
        }

        if (value.Stack is not null)
        {
            writer.WritePropertyName("stack");
            writer.WriteStartArray();

            foreach (var s in value.Stack)
                writer.WriteStringValue(s);

            writer.WriteEndArray();
        }

        writer.WritePropertyName("depth");
        writer.WriteNumberValue(value.Depth);

        writer.WritePropertyName("refund");
        writer.WriteNumberValue(value.Refund ?? 0L);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(value.Opcode);

        if (value.Error is not null)
        {
            writer.WritePropertyName("error");
            writer.WriteStringValue(value.Error);
        }

        writer.WriteEndObject();

        // Before writing a new line, flush and reset the writer
        // to avoid adding comma (depth tracking)
        writer.Flush();
        writer.Reset();
        writer.WriteRawValue(Environment.NewLine, true);
        // After writing the new line, flush and reset the writer again
        // to avoid adding comma on writer reuse
        writer.Flush();
        writer.Reset();
    }
}
