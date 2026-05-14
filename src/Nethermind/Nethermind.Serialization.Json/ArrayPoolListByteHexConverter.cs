// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Serializes an <see cref="ArrayPoolList{T}"/> of bytes as a hex string. Serialize-only — JSON
/// responses use pool-rented buffers; deserialization back into a pooled list is not supported.
/// </summary>
public sealed class ArrayPoolListByteHexConverter : JsonConverter<ArrayPoolList<byte>>
{
    public override ArrayPoolList<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(ArrayPoolListByteHexConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, ArrayPoolList<byte> value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.AsSpan(), skipLeadingZeros: false);
}
