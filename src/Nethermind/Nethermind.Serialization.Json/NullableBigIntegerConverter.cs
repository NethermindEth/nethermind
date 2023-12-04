// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json
{
    public class NullableBigIntegerConverter : JsonConverter<BigInteger?>
    {
        private static readonly BigIntegerConverter _bigIntegerConverter = new();

        public override BigInteger? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }

            return _bigIntegerConverter.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, BigInteger? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }

            _bigIntegerConverter.Write(writer, value.GetValueOrDefault(), options);
        }
    }
}
