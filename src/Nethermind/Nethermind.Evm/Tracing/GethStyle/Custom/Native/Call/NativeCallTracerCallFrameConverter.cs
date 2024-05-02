// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerCallFrameConverter : JsonConverter<NativeCallTracerCallFrame>
{
    public override void Write(Utf8JsonWriter writer, NativeCallTracerCallFrame value, JsonSerializerOptions options)
    {
        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        try
        {
            writer.WriteStartObject();

            ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
            writer.WritePropertyName("type"u8);
            JsonSerializer.Serialize(writer, value.Type.GetName(), options);

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
                JsonSerializer.Serialize(writer, value.Input.AsMemory(), options);
            }

            if (value.Output?.Count > 0)
            {
                writer.WritePropertyName("output"u8);
                JsonSerializer.Serialize(writer, value.Output.AsMemory(), options);
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

            if (value.Calls?.Count > 0)
            {
                writer.WritePropertyName("calls"u8);
                JsonSerializer.Serialize(writer, value.Calls.AsMemory(), options);
            }

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }
    }

    public override NativeCallTracerCallFrame Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException();
    }
}
