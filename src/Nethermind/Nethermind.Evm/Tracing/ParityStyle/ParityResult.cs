// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;

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
            JsonSerializerOptions options) => throw new NotImplementedException();

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
