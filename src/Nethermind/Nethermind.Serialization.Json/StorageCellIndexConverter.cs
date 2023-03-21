// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

#nullable enable

//namespace Nethermind.Serialization.Json
//{
//    

//    public class StorageCellIndexConverter : JsonConverter<UInt256>
//    {
//        public override void WriteJson(JsonWriter writer, UInt256 value, JsonSerializer serializer)
//        {
//            writer.WriteValue(value.ToHexString(false));
//        }

//        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, JsonSerializer serializer) =>
//            UInt256Converter.ReaderJson(reader);
//    }
//}

namespace Nethermind.Serialization.Json
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class StorageCellIndexConverter : JsonConverter<UInt256[]?>
    {
        private UInt256Converter _converter = new();

        public override UInt256[]? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return Array.Empty<UInt256>();
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException();
            }

            reader.Read();
            List<UInt256>? value = null;
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                value ??= new();
                value.Add(_converter.Read(ref reader, typeToConvert, options));
                reader.Read();
            }

            return value?.ToArray() ?? Array.Empty<UInt256>();
        }

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            UInt256[]? value,
            JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            Span<byte> bytes = stackalloc byte[32];
            for (int i = 0; i < value.Length; i++)
            {
                value[i].ToBigEndian(bytes);
                ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
            }
            writer.WriteEndArray();
        }
    }
}
