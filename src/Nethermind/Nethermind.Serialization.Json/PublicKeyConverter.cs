// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class PublicKeyConverter : JsonConverter<PublicKey>
    {
        public override void WriteJson(JsonWriter writer, PublicKey value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override PublicKey ReadJson(JsonReader reader, Type objectType, PublicKey existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return s is null ? null : new PublicKey(s);
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class PublicKeyJsonConverter : JsonConverter<PublicKey>
    {
        public override PublicKey Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            PublicKey publicKey,
            JsonSerializerOptions options)
        {
            ByteArrayJsonConverter.Convert(writer, publicKey.Bytes);
        }
    }
}
