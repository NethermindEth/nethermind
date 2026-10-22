// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityTraceResultConverter : JsonConverter<ParityTraceResult>
{
    public override ParityTraceResult Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException($"Cannot deserialize {nameof(ParityTraceActionConverter)}.");
        }

        ParityTraceResult value = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new ArgumentException($"Cannot deserialize {nameof(ParityTraceActionConverter)}.");
            }

            if (reader.ValueTextEquals("gasUsed"u8))
            {
                reader.Read();
                value.GasUsed = TypeInfoJsonSerializer.Deserialize<ulong>(ref reader, options);
            }
            else if (reader.ValueTextEquals("output"u8))
            {
                reader.Read();
                value.Output = TypeInfoJsonSerializer.Deserialize<byte[]?>(ref reader, options);
            }
            else if (reader.ValueTextEquals("address"u8))
            {
                reader.Read();
                value.Address = TypeInfoJsonSerializer.Deserialize<Address?>(ref reader, options);
            }
            else if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                value.Code = TypeInfoJsonSerializer.Deserialize<byte[]?>(ref reader, options);
            }

            reader.Read();
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ParityTraceResult value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.Address is not null)
        {
            writer.WritePropertyName("address"u8);
            TypeInfoJsonSerializer.Serialize(writer, value.Address, options);
            writer.WritePropertyName("code"u8);
            TypeInfoJsonSerializer.Serialize(writer, value.Code, options);
        }

        writer.WritePropertyName("gasUsed"u8);
        TypeInfoJsonSerializer.Serialize(writer, value.GasUsed, options);

        if (value.Address is null)
        {
            writer.WritePropertyName("output"u8);
            TypeInfoJsonSerializer.Serialize(writer, value.Output, options);
        }

        writer.WriteEndObject();
    }
}
