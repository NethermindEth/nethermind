// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class NullableLongConverter : JsonConverter<long?>
    {
        private readonly LongConverter _converter = new();

        public override long? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return _converter.Read(ref reader, typeToConvert, options);
        }

        public override void Write(
            Utf8JsonWriter writer,
            long? value,
            JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
            }
            else
            {
                _converter.Write(writer, value.GetValueOrDefault(), options);
            }
        }
    }

    public class NullableRawLongConverter : JsonConverter<long?>
    {
        private readonly LongConverter _converter = new();

        public override long? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return _converter.Read(ref reader, typeToConvert, options);
        }

        public override void Write(
            Utf8JsonWriter writer,
            long? value,
            JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteNumberValue(value.GetValueOrDefault());
            }
        }
    }
}
