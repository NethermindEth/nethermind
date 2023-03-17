// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;

    public class KeccakConverter : JsonConverter<Keccak>
    {
        public override void WriteJson(JsonWriter writer, Keccak value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value.Bytes, true));
            }
        }

        public override Keccak ReadJson(JsonReader reader, Type objectType, Keccak existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return string.IsNullOrWhiteSpace(s) ? null : new Keccak(Bytes.FromHexString(s));
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class KeccakJsonConverter : JsonConverter<Keccak>
    {
        public override Keccak Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            Keccak keccak,
            JsonSerializerOptions options)
        {
            ByteArrayJsonConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
        }
    }
}
