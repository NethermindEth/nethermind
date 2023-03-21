// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
//

//namespace Nethermind.Serialization.Json
//{
//    public class NullableBigIntegerConverter : JsonConverter<BigInteger?>
//    {
//        private BigIntegerConverter _bigIntegerConverter;

//        public NullableBigIntegerConverter()
//            : this(NumberConversion.Hex)
//        {
//        }

//        public NullableBigIntegerConverter(NumberConversion conversion)
//        {
//            _bigIntegerConverter = new BigIntegerConverter(conversion);
//        }

//        public override void WriteJson(JsonWriter writer, BigInteger? value, JsonSerializer serializer)
//        {
//            _bigIntegerConverter.WriteJson(writer, value.Value, serializer);
//        }

//        public override BigInteger? ReadJson(JsonReader reader, Type objectType, BigInteger? existingValue, bool hasExistingValue, JsonSerializer serializer)
//        {
//            if (reader.TokenType == JsonToken.Null || reader.Value is null)
//            {
//                return null;
//            }

//            return _bigIntegerConverter.ReadJson(reader, objectType, existingValue ?? 0, hasExistingValue, serializer);
//        }
//    }
//}
