// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
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
