// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json;

public class BanderwagonConverter: JsonConverter<Banderwagon>
{
    public override void WriteJson(JsonWriter writer, Banderwagon value, JsonSerializer serializer)
    {
        writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value.ToBytes(), true));
    }

    public override Banderwagon ReadJson(JsonReader reader, Type objectType, Banderwagon existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        string s = (string)reader.Value;
        return string.IsNullOrWhiteSpace(s) ? new Banderwagon() : Banderwagon.FromBytes(Bytes.FromHexString(s), subgroupCheck: false).Value;
    }
}
