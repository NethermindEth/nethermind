// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class BloomConverter : JsonConverter<Bloom>
{
    [SkipLocalsInit]
    public override Bloom? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        Span<byte> bytes = stackalloc byte[Bloom.ByteLength];
        if (ByteArrayConverter.TryConvertToSpan(ref reader, bytes, out int bytesWritten))
        {
            return new Bloom(bytes[..bytesWritten]);
        }

        byte[]? bytesArray = ByteArrayConverter.Convert(ref reader);
        return bytesArray is null ? null : new Bloom(bytesArray);
    }

    public override void Write(
        Utf8JsonWriter writer,
        Bloom bloom,
        JsonSerializerOptions options) => ByteArrayConverter.Convert(writer, bloom.ReadOnlyBytes, skipLeadingZeros: false);
}
