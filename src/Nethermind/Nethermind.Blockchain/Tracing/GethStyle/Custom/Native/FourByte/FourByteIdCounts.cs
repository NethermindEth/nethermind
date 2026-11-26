// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;

/// <summary>A 4byte id: the call selector and the size of the call data that follows it.</summary>
internal readonly record struct FourByteId(uint Selector, int Size)
{
    /// <summary>Longest UTF-8 form: "0x", eight hex digits, "-" and a non-negative <see cref="int"/>.</summary>
    public const int MaxUtf8Length = 2 + 8 + 1 + 10;

    /// <summary>Writes the id as <c>0x27dc297e-128</c> and returns the number of bytes written.</summary>
    public int FormatUtf8(Span<byte> destination)
    {
        destination[0] = (byte)'0';
        destination[1] = (byte)'x';
        Selector.TryFormat(destination[2..], out int selectorLength, "x8");
        int dash = 2 + selectorLength;
        destination[dash] = (byte)'-';
        Size.TryFormat(destination[(dash + 1)..], out int sizeLength);
        return dash + 1 + sizeLength;
    }
}

/// <summary>Result of the 4byte tracer, serialized as an object that maps each id to its count.</summary>
/// <remarks>The ids are written as UTF-8 property names from a stack buffer, so no key strings are created.</remarks>
[JsonConverter(typeof(FourByteIdCountsConverter))]
internal sealed class FourByteIdCounts(Dictionary<FourByteId, int> counts)
{
    public Dictionary<FourByteId, int> Counts { get; } = counts;
}

internal sealed class FourByteIdCountsConverter : JsonConverter<FourByteIdCounts>
{
    public override void Write(Utf8JsonWriter writer, FourByteIdCounts value, JsonSerializerOptions options)
    {
        Span<byte> key = stackalloc byte[FourByteId.MaxUtf8Length];
        writer.WriteStartObject();
        foreach (KeyValuePair<FourByteId, int> entry in value.Counts)
        {
            writer.WritePropertyName(key[..entry.Key.FormatUtf8(key)]);
            writer.WriteNumberValue(entry.Value);
        }

        writer.WriteEndObject();
    }

    public override FourByteIdCounts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
