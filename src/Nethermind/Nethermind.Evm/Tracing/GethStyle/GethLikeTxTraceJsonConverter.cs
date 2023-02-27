// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle;

internal class GethLikeTxTraceJsonConverter : JsonConverter<GethTxFileTraceEntry>
{
    public override GethTxFileTraceEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

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
        writer.WriteNumberValue((long)(value.MemorySize ?? 0));

        if (value.Memory?.Any() ?? false)
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
        writer.WriteNumberValue(value.Refund ?? 0);

        writer.WritePropertyName("opName");
        writer.WriteStringValue(value.Opcode);

        if (value.Error is not null)
        {
            writer.WritePropertyName("error");
            writer.WriteStringValue(value.Error);
        }

        writer.WriteEndObject();
    }
}
