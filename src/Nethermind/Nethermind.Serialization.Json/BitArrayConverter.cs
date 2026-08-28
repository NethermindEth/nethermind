// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class BitArrayConverter : JsonConverter<BitArray>
{
    public override BitArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[] bytes = ByteArrayConverter.ConvertData(ref reader);
        return bytes is null ? throw new JsonException("Expected a hex-encoded bit array.") : new BitArray(bytes);
    }

    public override void Write(Utf8JsonWriter writer, BitArray value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        byte[] bytes = new byte[(value.Length + 7) / 8];
        value.CopyTo(bytes, 0);
        ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
    }
}
