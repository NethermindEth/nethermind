// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;

namespace Nethermind.Core.JsonConverters;

/// <summary>
/// Deserializes a nullable <see cref="ulong"/> JSON-RPC QUANTITY field.
/// </summary>
/// <remarks>
/// When <see cref="JsonRpcQuantityFormat.StrictMode"/> is <see langword="true"/> (the default),
/// hex strings that carry leading zero digits (e.g. <c>"0x0b"</c>) are rejected per EIP-1474.
/// Lenient mode (StrictMode == false) accepts them for backwards compatibility.
/// </remarks>
public class NullableQuantityULongConverter : JsonConverter<ulong?>
{
    [SkipLocalsInit]
    public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.String) ThrowJsonException();

        int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        // "0xffffffffffffffff" (18 bytes) is the longest valid ulong QUANTITY string.
        if (length is 0 or > 18) ThrowJsonException();

        if (reader.HasValueSequence)
        {
            Span<byte> span = stackalloc byte[length];
            reader.ValueSequence.CopyTo(span);
            return ReadHex(span);
        }

        return ReadHex(reader.ValueSpan);
    }

    internal static ulong ReadHex(ReadOnlySpan<byte> s)
    {
        if (s.SequenceEqual("0x0"u8)) return 0;

        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (JsonRpcQuantityFormat.StrictMode && s.Length > 1 && s[0] == (byte)'0') ThrowLeadingZero();

            if (ulong.TryParse(s, NumberStyles.AllowHexSpecifier, null, out ulong value)) return value;
        }

        ThrowJsonException();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue($"0x{value.Value:x}");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowJsonException() => throw new JsonException();

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowLeadingZero() =>
        throw new SafePublicMessageFormatException("hex number with leading zero digits");
}
