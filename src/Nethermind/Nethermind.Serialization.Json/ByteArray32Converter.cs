// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;
    public class Bytes32Converter : JsonConverter<byte[]>
    {
        public override void WriteJson(JsonWriter writer, byte[] value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToHexString(false).PadLeft(64, '0')));
        }

        public override byte[] ReadJson(
            JsonReader reader,
            Type objectType,
            byte[] existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            if (s is null)
            {
                return null;
            }

            return Bytes.FromHexString(s);
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class Byte32JsonConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            byte[] bytes,
            JsonSerializerOptions options)
        {
            Span<byte> data = (bytes is null || bytes.Length < 32) ? stackalloc byte[32] : bytes;
            if (bytes is not null && bytes.Length < 32)
            {
                bytes.AsSpan().CopyTo(data[(32 - bytes.Length)..]);
            }

            ByteArrayJsonConverter.Convert(writer, data, skipLeadingZeros: false);
        }
    }
}
