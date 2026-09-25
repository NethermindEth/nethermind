// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// How <see cref="TraceFilterForRpc.FromAddress"/> and <see cref="TraceFilterForRpc.ToAddress"/> combine.
/// An omitted or null list never restricts; within a list any address matches.
/// </summary>
[JsonConverter(typeof(TraceFilterModeConverter))]
public enum TraceFilterMode
{
    /// <summary>A record must match every populated list.</summary>
    Intersection,

    /// <summary>A record must match at least one populated list; with no list set, every record matches.</summary>
    Union,
}

/// <summary>
/// Reads only the exact <c>"intersection"</c> and <c>"union"</c> strings; any other value is invalid params.
/// </summary>
public sealed class TraceFilterModeConverter : JsonConverter<TraceFilterMode>
{
    public override TraceFilterMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (reader.ValueTextEquals("intersection"u8)) return TraceFilterMode.Intersection;
            if (reader.ValueTextEquals("union"u8)) return TraceFilterMode.Union;
        }

        throw new SafePublicMessageFormatException("invalid trace filter mode, expected \"intersection\" or \"union\"");
    }

    public override void Write(Utf8JsonWriter writer, TraceFilterMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TraceFilterMode.Intersection => "intersection"u8,
            TraceFilterMode.Union => "union"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        });
}
