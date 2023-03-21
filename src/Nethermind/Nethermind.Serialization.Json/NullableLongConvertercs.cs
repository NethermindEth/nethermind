// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

//namespace Nethermind.Serialization.Json
//{
//    

//    public class NullableLongConverter : JsonConverter<long?>
//    {
//        private LongConverter _longConverter;

//        public NullableLongConverter()
//            : this(NumberConversion.Hex)
//        {
//        }

//        public NullableLongConverter(NumberConversion conversion)
//        {
//            _longConverter = new LongConverter(conversion);
//        }

//        public override void WriteJson(JsonWriter writer, long? value, JsonSerializer serializer)
//        {
//            if (!value.HasValue)
//            {
//                writer.WriteNull();
//                return;
//            }

//            _longConverter.WriteJson(writer, value.Value, serializer);
//        }

//        public override long? ReadJson(JsonReader reader, Type objectType, long? existingValue, bool hasExistingValue, JsonSerializer serializer)
//        {
//            if (reader.TokenType == JsonToken.Null || reader.Value is null)
//            {
//                return null;
//            }

//            return _longConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
//        }
//    }
//}

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
