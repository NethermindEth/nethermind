// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class BloomConverter : JsonConverter<Bloom>
    {
        public override void WriteJson(JsonWriter writer, Bloom value, JsonSerializer serializer)
        {
            writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value.Bytes, true));
        }

        public override Bloom ReadJson(JsonReader reader, Type objectType, Bloom existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return s is null ? null : new Bloom(Bytes.FromHexString(s));
        }
    }
}
