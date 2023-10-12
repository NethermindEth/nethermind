// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

#nullable enable

namespace Nethermind.Serialization.Json
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class StorageCellIndexConverter : JsonConverter<IEnumerable<UInt256>?>
    {
        private UInt256Converter _converter = new();

        public override IEnumerable<UInt256>? Read(
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
            IEnumerable<UInt256>? values,
            JsonSerializerOptions options)
        {
            if (values is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            Span<byte> bytes = stackalloc byte[32];

            foreach (var value in values)
            {
                value.ToBigEndian(bytes);
                ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
            }
            writer.WriteEndArray();
        }
    }
}
