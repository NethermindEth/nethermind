// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class IdConverter : JsonConverter<object>
    {
        public override object Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out long value))
                {
                    return value;
                }
                if (reader.TryGetDecimal(out decimal val) && val.Scale == 0)
                {
                    return val;
                }

                throw new NotSupportedException();
            }

            return reader.GetString();
        }

        public override void Write(
            Utf8JsonWriter writer,
            object value,
            JsonSerializerOptions options)
        {

            switch (value)
            {
                case int typedValue:
                    writer.WriteNumberValue(typedValue);
                    break;
                case long typedValue:
                    writer.WriteNumberValue(typedValue);
                    break;
                case decimal typedValue:
                    writer.WriteNumberValue(typedValue);
                    break;
                case BigInteger typedValue:
                    writer.WriteNumberValue((decimal)typedValue);
                    break;
                case string typedValue:
                    writer.WriteStringValue(typedValue);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public override bool CanConvert(Type objectType)
        {
            return true;
        }
    }
}
