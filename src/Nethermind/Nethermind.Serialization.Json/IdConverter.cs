// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class IdConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            switch (value)
            {
                case int typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case long typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case BigInteger typedValue:
                    writer.WriteValue(typedValue);
                    break;
                case string typedValue:
                    writer.WriteValue(typedValue);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer:
                    return reader.Value;
                case JsonToken.String:
                    return reader.Value as string;
                case JsonToken.Null:
                    return null;
                default:
                    throw new NotSupportedException($"{reader.TokenType}");
            }
        }

        public override bool CanConvert(Type objectType)
        {
            return true;
        }
    }
}
