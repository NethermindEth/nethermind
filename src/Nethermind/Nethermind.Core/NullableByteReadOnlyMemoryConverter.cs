// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class NullableByteReadOnlyMemoryConverter : JsonConverter<ReadOnlyMemory<byte>?>
{
    public override ReadOnlyMemory<byte>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(ref reader);

    public override void Write(
        Utf8JsonWriter writer,
        ReadOnlyMemory<byte>? bytes,
        JsonSerializerOptions options) =>
        ByteArrayConverter.Convert(writer, bytes is null ? ReadOnlySpan<byte>.Empty : bytes.Value.Span, skipLeadingZeros: false);
}
