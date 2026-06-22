// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.JsonConverters;

/// <summary>
/// Deserializes a nullable <see cref="UInt256"/> QUANTITY field following EIP-1474 strict rules:
/// the hex string must not have leading zero digits (the only valid zero representation is <c>"0x0"</c>).
/// </summary>
public class NullableQuantityUInt256Converter : JsonConverter<UInt256?>
{
    [SkipLocalsInit]
    public override UInt256? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            ThrowJsonException();
        }

        int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length is 0 or > 78)
        {
            ThrowJsonException();
        }

        if (reader.HasValueSequence)
        {
            Span<byte> span = stackalloc byte[length];
            reader.ValueSequence.CopyTo(span);
            return ReadHex((ReadOnlySpan<byte>)span);
        }

        return ReadHex(reader.ValueSpan);
    }

    internal static UInt256 ReadHex(ReadOnlySpan<byte> hex)
    {
        if (hex.SequenceEqual("0x0"u8))
        {
            return default;
        }

        if (hex.StartsWith("0x"u8))
        {
            hex = hex[2..];
            // EIP-1474: QUANTITY hex strings must not have leading zero digits.
            if (JsonRpcQuantityFormat.StrictMode && hex.Length > 1 && hex[0] == (byte)'0')
            {
                ThrowLeadingZero();
            }
        }
        else if (hex[0] != (byte)'0')
        {
            if (UInt256.TryParse(Encoding.UTF8.GetString(hex), out UInt256 result))
            {
                return result;
            }
        }

        Span<byte> bytes = stackalloc byte[32];
        int length = (hex.Length >> 1) + hex.Length % 2;
        Bytes.FromUtf8HexString(hex, bytes[(32 - length)..]);
        ReadOnlySpan<byte> readOnlyBytes = bytes;
        return new UInt256(in readOnlyBytes, isBigEndian: true);
    }

    public override void Write(Utf8JsonWriter writer, UInt256? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Delegate to the standard UInt256 writer via the registered converter.
        JsonSerializer.Serialize(writer, value.Value, options);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowJsonException() => throw new JsonException();

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowLeadingZero() =>
        throw new SafePublicMessageFormatException("hex number with leading zero digits");
}
