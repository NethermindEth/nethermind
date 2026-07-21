// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Serialization.Json;

/// <summary>Serializes <see cref="HexBytes"/> as a 0x-prefixed hex JSON string.</summary>
public sealed class HexBytesConverter : JsonConverter<HexBytes>
{
    /// <inheritdoc/>
    public override HexBytes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader);
        return new HexBytes(bytes ?? []);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, HexBytes value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.Bytes.Span, skipLeadingZeros: false);
}
