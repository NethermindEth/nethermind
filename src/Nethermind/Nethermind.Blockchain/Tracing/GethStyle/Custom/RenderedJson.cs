// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom;

/// <summary>
/// A tracer result already rendered to UTF-8 JSON, written to the response verbatim.
/// </summary>
[JsonConverter(typeof(RenderedJsonConverter))]
internal sealed class RenderedJson(byte[] utf8)
{
    public ReadOnlyMemory<byte> Utf8 => utf8;
}

internal sealed class RenderedJsonConverter : JsonConverter<RenderedJson>
{
    public override void Write(Utf8JsonWriter writer, RenderedJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Utf8.Span, skipInputValidation: true);

    public override RenderedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
