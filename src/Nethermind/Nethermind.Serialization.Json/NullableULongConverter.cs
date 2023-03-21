// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

//namespace Nethermind.Serialization.Json
//{
//    

//    public class NullableULongConverter : JsonConverter<ulong?>
//    {
//        private readonly ULongConverter _ulongConverter;

//        public NullableULongConverter()
//            : this(NumberConversion.Hex)
//        {
//        }

//        public NullableULongConverter(NumberConversion conversion)
//        {
//            _ulongConverter = new ULongConverter(conversion);
//        }

//        public override void WriteJson(JsonWriter writer, ulong? value, JsonSerializer serializer)
//        {
//            if (!value.HasValue)
//            {
//                writer.WriteNull();
//                return;
//            }

//            _ulongConverter.WriteJson(writer, value.Value, serializer);
//        }

//        public override ulong? ReadJson(JsonReader reader, Type objectType, ulong? existingValue, bool hasExistingValue,
//            JsonSerializer serializer)
//        {
//            if (reader.TokenType == JsonToken.Null)
//            {
//                return null;
//            }

//            return _ulongConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
//        }
//    }
//}

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class NullableULongConverter : JsonConverter<ulong?>
    {
        private readonly ULongConverter _converter = new();

        public override ulong? Read(
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
            ulong? value,
            JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
                return;
            }

            _converter.Write(writer, value.GetValueOrDefault(), options);
        }
    }
}
