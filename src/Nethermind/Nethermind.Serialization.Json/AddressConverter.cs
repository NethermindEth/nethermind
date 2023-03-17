// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;

    public class AddressConverter : JsonConverter<Address>
    {
        public override void WriteJson(JsonWriter writer, Address value, JsonSerializer serializer)
        {
            writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value.Bytes, true));
        }

        public override Address ReadJson(JsonReader reader, Type objectType, Address existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return string.IsNullOrEmpty(s) ? null : new Address(s);
        }
    }
}

namespace Nethermind.Serialization.Json
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class AddressJsonConverter : JsonConverter<Address>
    {
        public override Address Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            Address address,
            JsonSerializerOptions options)
        {
            ByteArrayJsonConverter.Convert(writer, address.Bytes, skipLeadingZeros: false);
        }
    }
}
