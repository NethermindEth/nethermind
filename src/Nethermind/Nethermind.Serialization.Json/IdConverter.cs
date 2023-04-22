// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

//namespace Nethermind.Serialization.Json
//{
//    

//    public class IdConverter : JsonConverter
//    {
//        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
//        {
//            switch (value)
//            {
//                case int typedValue:
//                    writer.WriteValue(typedValue);
//                    break;
//                case long typedValue:
//                    writer.WriteValue(typedValue);
//                    break;
//                case BigInteger typedValue:
//                    writer.WriteValue(typedValue);
//                    break;
//                case string typedValue:
//                    writer.WriteValue(typedValue);
//                    break;
//                default:
//                    throw new NotSupportedException();
//            }
//        }

//        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
//        {
//            switch (reader.TokenType)
//            {
//                case JsonToken.Integer:
//                    return reader.Value;
//                case JsonToken.String:
//                    return reader.Value as string;
//                case JsonToken.Null:
//                    return null;
//                default:
//                    throw new NotSupportedException($"{reader.TokenType}");
//            }
//        }

//        public override bool CanConvert(Type objectType)
//        {
//            return true;
//        }
//    }
//}

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
