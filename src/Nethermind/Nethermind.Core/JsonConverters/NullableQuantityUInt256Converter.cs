// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
/// Deserializes a nullable <see cref="UInt256"/> JSON-RPC QUANTITY field.
/// </summary>
/// <remarks>
/// When <see cref="JsonRpcQuantityFormat.StrictMode"/> is <see langword="true"/> (the default),
/// hex strings that carry leading zero digits (e.g. <c>"0x0b"</c>) are rejected per EIP-1474.
/// Lenient mode (StrictMode == false) accepts them for backwards compatibility.
/// </remarks>
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
        // 78 covers the longest decimal UInt256 (78 digits); hex is further bounded to 64 digits inside ReadHex.
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
            if (hex.Length > 64) // UInt256 is 32 bytes = 64 hex digits max
            {
                ThrowJsonException();
            }
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

        if (value.Value.IsZero)
        {
            writer.WriteStringValue("0x0"u8);
            return;
        }

        Span<byte> bytes = stackalloc byte[32];
        value.Value.ToBigEndian(bytes);
        int start = bytes.IndexOfAnyExcept((byte)0);
        string hex = Convert.ToHexStringLower(bytes[start..]);
        // Strip leading zero nibble: byte 0x0b → hex "0b" → QUANTITY "0xb"
        writer.WriteStringValue(hex[0] == '0' ? "0x" + hex[1..] : "0x" + hex);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowJsonException() => throw new JsonException();

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowLeadingZero() =>
        throw new SafePublicMessageFormatException("hex number with leading zero digits");
}
