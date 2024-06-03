// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Verkle.Curve;


namespace Nethermind.Serialization.Json;

public class BanderwagonConverter : System.Text.Json.Serialization.JsonConverter<Banderwagon>
{
    public override Banderwagon Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var data = ByteArrayConverter.Convert(ref reader);
        return data is null ? new Banderwagon() : Banderwagon.FromBytes(data, subgroupCheck: false)!.Value;
    }

    public override void Write(Utf8JsonWriter writer, Banderwagon value, JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, value.ToBytes(), skipLeadingZeros: false);
    }
}
