// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;

    public class NullableLongConverter : JsonConverter<long?>
    {
        private LongConverter _longConverter;

        public NullableLongConverter()
            : this(NumberConversion.Hex)
        {
        }

        public NullableLongConverter(NumberConversion conversion)
        {
            _longConverter = new LongConverter(conversion);
        }

        public override void WriteJson(JsonWriter writer, long? value, JsonSerializer serializer)
        {
            if (!value.HasValue)
            {
                writer.WriteNull();
                return;
            }

            _longConverter.WriteJson(writer, value.Value, serializer);
        }

        public override long? ReadJson(JsonReader reader, Type objectType, long? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null || reader.Value is null)
            {
                return null;
            }

            return _longConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class NullableLongJsonConverter : JsonConverter<long?>
    {
        private LongJsonConverter _converter = new();

        public override long? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

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

    public class NullableRawLongJsonConverter : JsonConverter<long?>
    {
        public override long? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

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
