// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxTraceConverter : JsonConverter<GethLikeTxTrace>
{
    public override GethLikeTxTrace Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // If not an object, it should be a custom tracer result which we can't deserialize properly
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Custom tracer result object is not supported. Expected start of object");
        }

        var trace = new GethLikeTxTrace();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.ValueTextEquals("gas"u8))
            {
                reader.Read();
                NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
                ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
                try
                {
                    trace.Gas = JsonSerializer.Deserialize<long>(ref reader, options);
                }
                finally
                {
                    ForcedNumberConversion.ForcedConversion.Value = previousValue;
                }

                continue;
            }

            if (reader.ValueTextEquals("failed"u8))
            {
                reader.Read();
                trace.Failed = JsonSerializer.Deserialize<bool>(ref reader, options);
                continue;
            }

            if (reader.ValueTextEquals("returnValue"u8))
            {
                reader.Read();
                trace.ReturnValue = JsonSerializer.Deserialize<byte[]>(ref reader, options);
                continue;
            }

            if (reader.ValueTextEquals("structLogs"u8))
            {
                reader.Read();
                trace.Entries = JsonSerializer.Deserialize<List<GethTxTraceEntry>>(ref reader, options);
                continue;
            }

            // If we find any unexpected property, it should be a custom tracer result which we can't deserialize properly
            throw new JsonException($"Custom tracer result object is not supported. Unexpected property: {reader.GetString()}");
        }

        return trace;
    }

    public override void Write(
        Utf8JsonWriter writer,
        GethLikeTxTrace? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.CustomTracerResult is not null)
        {
            JsonSerializer.Serialize(writer, value.CustomTracerResult, options);
            return;
        }

        writer.WriteStartObject();

        NumberConversion? previousValue = ForcedNumberConversion.ForcedConversion.Value;
        ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Raw;
        try
        {
            writer.WritePropertyName("gas"u8);
            JsonSerializer.Serialize(writer, value.Gas, options);
        }
        finally
        {
            ForcedNumberConversion.ForcedConversion.Value = previousValue;
        }

        writer.WritePropertyName("failed"u8);
        JsonSerializer.Serialize(writer, value.Failed, options);

        writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(writer, value.ReturnValue, options);

        writer.WritePropertyName("structLogs"u8);
        JsonSerializer.Serialize(writer, value.Entries, options);

        writer.WriteEndObject();
    }
}
