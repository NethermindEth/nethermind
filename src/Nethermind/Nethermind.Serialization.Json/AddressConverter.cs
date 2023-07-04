// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
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
