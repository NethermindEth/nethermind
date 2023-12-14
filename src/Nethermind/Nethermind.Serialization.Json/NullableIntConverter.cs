// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json
{
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class NullableIntConverter : JsonConverter<int?>
    {
        private readonly IntConverter _converter = new();

        public override int? Read(
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

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            int? value,
            JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
            }

            _converter.Write(writer, value.GetValueOrDefault(), options);
        }
    }
}
