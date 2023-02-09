// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class NullableUInt256Converter : JsonConverter<UInt256?>
    {
        private UInt256Converter _uInt256Converter;

        public NullableUInt256Converter()
            : this(NumberConversion.Hex)
        {
        }

        public NullableUInt256Converter(NumberConversion conversion)
        {
            _uInt256Converter = new UInt256Converter(conversion);
        }

        public override void WriteJson(JsonWriter writer, UInt256? value, JsonSerializer serializer)
        {
            if (!value.HasValue)
            {
                writer.WriteNull();
                return;
            }

            _uInt256Converter.WriteJson(writer, value.Value, serializer);
        }

        public override UInt256? ReadJson(JsonReader reader, Type objectType, UInt256? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null || reader.Value is null)
            {
                return null;
            }

            return _uInt256Converter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
        }
    }
}
