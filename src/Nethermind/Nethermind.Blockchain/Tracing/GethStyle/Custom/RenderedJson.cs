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
    /// <summary>
    /// Writes the pre-rendered bytes after walking them with the response serializer's depth limit, so a
    /// configured <see cref="JsonSerializerOptions.MaxDepth"/> still bounds script results.
    /// </summary>
    /// <remarks>
    /// The walk counts depth from the result's own root and the raw write bypasses the writer's accounting, so the
    /// limit bounds the script result rather than the whole response: the response envelope adds its own levels
    /// on top. The writer's built-in validation is not used because it applies the default depth, not the
    /// configured one.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, RenderedJson value, JsonSerializerOptions options)
    {
        Utf8JsonReader reader = new(value.Utf8.Span, new JsonReaderOptions { MaxDepth = options.MaxDepth });
        while (reader.Read())
        {
        }

        writer.WriteRawValue(value.Utf8.Span, skipInputValidation: true);
    }

    public override RenderedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
