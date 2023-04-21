// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    [JsonConverter(typeof(ParityTraceResultConverter))]
    public class ParityTraceResult
    {
        public long GasUsed { get; set; }
        public byte[]? Output { get; set; }
        public Address? Address { get; set; }
        public byte[]? Code { get; set; }
    }

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

            var value = new ParityTraceResult();

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
                    value.GasUsed = JsonSerializer.Deserialize<long>(ref reader, options);
                }
                else if (reader.ValueTextEquals("output"u8))
                {
                    reader.Read();
                    value.Output = JsonSerializer.Deserialize<byte[]?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("address"u8))
                {
                    reader.Read();
                    value.Address = JsonSerializer.Deserialize<Address?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("code"u8))
                {
                    reader.Read();
                    value.Code = JsonSerializer.Deserialize<byte[]?>(ref reader, options);
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
                JsonSerializer.Serialize(writer, value.Address, options);
                writer.WritePropertyName("code"u8);
                JsonSerializer.Serialize(writer, value.Code, options);
            }

            writer.WritePropertyName("gasUsed"u8);
            JsonSerializer.Serialize(writer, value.GasUsed, options);

            if (value.Address is null)
            {
                writer.WritePropertyName("output"u8);
                JsonSerializer.Serialize(writer, value.Output, options);
            }

            writer.WriteEndObject();
        }
    }
}
