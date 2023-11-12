// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class KeccakConverter : JsonConverter<Hash256>
    {
        public override void WriteJson(JsonWriter writer, Hash256 value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteValue(value.Bytes.ToHexString(true));
            }
        }

        public override Hash256 ReadJson(JsonReader reader, Type objectType, Hash256 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return string.IsNullOrWhiteSpace(s) ? null : new Hash256(Bytes.FromHexString(s));
        }
    }
}
