// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json;

public class StemConverter: JsonConverter<Stem>
{
    public override void WriteJson(JsonWriter writer, Stem value, JsonSerializer serializer)
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

    public override Stem ReadJson(JsonReader reader, Type objectType, Stem existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        string s = (string)reader.Value;
        return string.IsNullOrWhiteSpace(s) ? null : new Stem(Bytes.FromHexString(s));
    }
}
