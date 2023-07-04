// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
