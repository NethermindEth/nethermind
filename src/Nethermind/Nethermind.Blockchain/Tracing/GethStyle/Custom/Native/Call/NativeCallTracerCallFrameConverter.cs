// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
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

            using ArrayPoolListRef<(NativeCallTracerCallFrame Frame, int NextChild)> work = new(16);
            work.Add((value, -1));

            while (work.Count > 0)
            {
                (NativeCallTracerCallFrame frame, int nextChild) = work.RemoveLast();

                if (nextChild == -1)
                {
                    WriteFrameHeader(writer, frame, options);

                    if (frame.CallsSpan.Length > 0)
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

                ReadOnlySpan<NativeCallTracerCallFrame> calls = frame.CallsSpan;
                if (nextChild < calls.Length)
                {
                    work.Add((frame, nextChild + 1));
                    work.Add((calls[nextChild], -1));
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
        WriteValue(writer, Enum.GetName(value.Type), options);

        writer.WritePropertyName("from"u8);
        WriteValue(writer, value.From, options);

        if (value.To is not null)
        {
            writer.WritePropertyName("to"u8);
            WriteValue(writer, value.To, options);
        }

        if (value.Value is not null)
        {
            writer.WritePropertyName("value"u8);
            WriteValue(writer, value.Value, options);
        }

        writer.WritePropertyName("gas"u8);
        WriteValue(writer, value.Gas, options);

        writer.WritePropertyName("gasUsed"u8);
        WriteValue(writer, value.GasUsed, options);

        if (value.Eip8037Gas is { } eip8037Gas)
        {
            writer.WritePropertyName("regularGasUsed"u8);
            WriteValue(writer, eip8037Gas.RegularGasUsed, options);
            writer.WritePropertyName("stateGasUsed"u8);
            WriteValue(writer, eip8037Gas.StateGasUsed, options);
            writer.WritePropertyName("gasRefund"u8);
            WriteValue(writer, eip8037Gas.GasRefund, options);
        }

        writer.WritePropertyName("input"u8);
        if (value.Input.IsEmpty)
        {
            writer.WriteStringValue("0x"u8);
        }
        else
        {
            WriteValue(writer, value.Input, options);
        }

        if (!value.Output.IsEmpty)
        {
            writer.WritePropertyName("output"u8);
            WriteValue(writer, value.Output, options);
        }

        if (value.Error is not null)
        {
            writer.WritePropertyName("error"u8);
            WriteValue(writer, value.Error, options);
        }

        if (value.RevertReason is not null)
        {
            writer.WritePropertyName("revertReason"u8);
            WriteValue(writer, value.RevertReason, options);
        }

        if (value.Logs?.Count > 0)
        {
            WriteLogs(writer, value.Logs.AsSpan(), options);
        }
    }

    private static void WriteLogs(Utf8JsonWriter writer, ReadOnlySpan<NativeCallTracerLogEntry> logs, JsonSerializerOptions options)
    {
        writer.WritePropertyName("logs"u8);
        writer.WriteStartArray();
        foreach (NativeCallTracerLogEntry log in logs)
        {
            writer.WriteStartObject();

            if (log.Address is not null)
            {
                writer.WritePropertyName("address"u8);
                WriteValue(writer, log.Address, options);
            }

            if (log.Data is not null)
            {
                writer.WritePropertyName("data"u8);
                WriteValue(writer, log.Data, options);
            }

            if (log.Topics is not null)
            {
                writer.WritePropertyName("topics"u8);
                WriteValue(writer, log.Topics, options);
            }

            writer.WritePropertyName("index"u8);
            WriteValue(writer, log.Index, options);

            writer.WritePropertyName("position"u8);
            WriteValue(writer, log.Position, options);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // A root-level serializer call per property would set up a write stack and flush the writer each time.
    private static void WriteValue<T>(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        ((JsonConverter<T>)TypeInfoJsonSerializer.GetTypeInfo<T>(options).Converter).Write(writer, value, options);
    }

    public override NativeCallTracerCallFrame Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
