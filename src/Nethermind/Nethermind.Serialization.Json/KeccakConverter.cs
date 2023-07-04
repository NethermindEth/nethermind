// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
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
                writer.WriteValue(value.Bytes.ToHexString(true));
            }
        }

        public override Keccak ReadJson(JsonReader reader, Type objectType, Keccak existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return string.IsNullOrWhiteSpace(s) ? null : new Keccak(Bytes.FromHexString(s));
        }
    }
}
