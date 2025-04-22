// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Verkle;

namespace Nethermind.Serialization.Json;

public class StemConverter : JsonConverter<Stem>
{
    public override Stem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var data = ByteArrayConverter.Convert(ref reader);
        return data is null ? null : new Stem(data);
    }

    public override void Write(Utf8JsonWriter writer, Stem value, JsonSerializerOptions options)
    {
        ByteArrayConverter.Convert(writer, value.Bytes, skipLeadingZeros: false);
    }
}
