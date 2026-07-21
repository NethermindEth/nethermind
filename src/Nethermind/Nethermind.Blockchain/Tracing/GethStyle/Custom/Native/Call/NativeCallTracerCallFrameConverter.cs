// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerCallFrameConverter : JsonConverter<NativeCallTracerCallFrame>
{
    public override void Write(Utf8JsonWriter writer, NativeCallTracerCallFrame value, JsonSerializerOptions options)
    {
        NumberConversion previousValue = ForcedNumberConversion.Value;
        try
        {
            ForcedNumberConversion.Value = NumberConversion.Hex;

            Stack<(NativeCallTracerCallFrame Frame, int NextChild)> work = new();
            work.Push((value, -1));

            while (work.Count > 0)
            {
                (NativeCallTracerCallFrame frame, int nextChild) = work.Pop();

                if (nextChild == -1)
                {
                    WriteFrameHeader(writer, frame, options);

                    if (frame.Calls is { Count: > 0 })
                    {
                        writer.WritePropertyName("calls"u8);
                        writer.WriteStartArray();
                        nextChild = 0;
                    }
                    else
                    {
                        writer.WriteEndObject();
                        continue;
                    }
                }

                if (nextChild < frame.Calls.Count)
                {
                    NativeCallTracerCallFrame child = frame.Calls[nextChild];
                    work.Push((frame, nextChild + 1));
                    work.Push((child, -1));
                }
                else
                {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }

    private static void WriteFrameHeader(Utf8JsonWriter writer, NativeCallTracerCallFrame value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("type"u8);
        JsonSerializer.Serialize(writer, Enum.GetName(value.Type), options);

        writer.WritePropertyName("from"u8);
        JsonSerializer.Serialize(writer, value.From, options);

        if (value.To is not null)
        {
            writer.WritePropertyName("to"u8);
            JsonSerializer.Serialize(writer, value.To, options);
        }

        if (value.Value is not null)
        {
            writer.WritePropertyName("value"u8);
            JsonSerializer.Serialize(writer, value.Value, options);
        }

        writer.WritePropertyName("gas"u8);
        JsonSerializer.Serialize(writer, value.Gas, options);

        writer.WritePropertyName("gasUsed"u8);
        JsonSerializer.Serialize(writer, value.GasUsed, options);

        writer.WritePropertyName("input"u8);
        if (value.Input is null || value.Input.Count == 0)
        {
            writer.WriteStringValue("0x"u8);
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Input.AsReadOnlyMemory(), options);
        }

        if (value.Output?.Count > 0)
        {
            writer.WritePropertyName("output"u8);
            JsonSerializer.Serialize(writer, value.Output.AsReadOnlyMemory(), options);
        }

        if (value.Error is not null)
        {
            writer.WritePropertyName("error"u8);
            JsonSerializer.Serialize(writer, value.Error, options);
        }

        if (value.RevertReason is not null)
        {
            writer.WritePropertyName("revertReason"u8);
            JsonSerializer.Serialize(writer, value.RevertReason, options);
        }

        if (value.Logs?.Count > 0)
        {
            writer.WritePropertyName("logs"u8);
            JsonSerializer.Serialize(writer, value.Logs.AsMemory(), options);
        }
    }

    public override NativeCallTracerCallFrame Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
