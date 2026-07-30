// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Serializes an <see cref="ArrayPoolList{T}"/> of bytes as a hex string and deserializes a hex string
/// back into a pool-rented <see cref="ArrayPoolList{T}"/>. <see cref="Read"/> transfers ownership to the
/// caller, which MUST dispose the result; the JSON-RPC pipeline does not dispose deserialized parameters.
/// </summary>
public sealed class ArrayPoolListByteHexConverter : JsonConverter<ArrayPoolList<byte>>
{
    public override ArrayPoolList<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ByteArrayConverter.ConvertToArrayPoolList(ref reader);

    public override void Write(Utf8JsonWriter writer, ArrayPoolList<byte> value, JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, value.AsSpan(), skipLeadingZeros: false);
}
