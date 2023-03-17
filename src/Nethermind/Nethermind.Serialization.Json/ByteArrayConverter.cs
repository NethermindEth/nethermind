// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;

    public class ByteArrayConverter : JsonConverter<byte[]>
    {
        public override void WriteJson(JsonWriter writer, byte[] value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value, true));
            }
        }

        public override byte[] ReadJson(JsonReader reader, Type objectType, byte[] existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            string s = (string)reader.Value;
            return Bytes.FromHexString(s);
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Buffers;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class ByteArrayJsonConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            byte[] bytes,
            JsonSerializerOptions options)
        {
            Convert(writer, bytes, skipLeadingZeros: false);
        }

        [SkipLocalsInit]
        public static void Convert(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, bool skipLeadingZeros = true)
        {
            const int maxStackLength = 128;
            const int stackLength = 256;

            int leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingZeros() : 0;
            int length = bytes.Length * 2 - leadingNibbleZeros + 4;

            byte[] array = null;
            if (length > maxStackLength)
            {
                array = ArrayPool<byte>.Shared.Rent(length);
            }

            Span<byte> hex = (array is null ? stackalloc byte[stackLength] : array)[..length];
            hex[^1] = (byte)'"';
            hex[0] = (byte)'"';
            hex[1] = (byte)'0';
            hex[2] = (byte)'x';

            Span<byte> output = hex[3..^1];

            bool extraNibble = (leadingNibbleZeros & 1) != 0;
            ReadOnlySpan<byte> input = bytes.Slice(leadingNibbleZeros / 2);
            input.OutputBytesToByteHex(output, extraNibble: (leadingNibbleZeros & 1) != 0);
            writer.WriteRawValue(hex, skipInputValidation: true);

            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}
