// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

public class UInt256Converter : JsonConverter<UInt256>
{
    public override UInt256 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (UInt256)reader.GetUInt64();
        }
        if (reader.TokenType != JsonTokenType.String)
        {
            ThrowInvalidOperationException();
        }

        ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        return Read(hex);
    }

    public static UInt256 Read(ReadOnlySpan<byte> hex)
    {
        if (hex.SequenceEqual("0x0"u8))
        {
            return default;
        }

        if (hex.StartsWith("0x"u8))
        {
            hex = hex[2..];
        }
        else if (hex[0] != (byte)'0')
        {
            if (UInt256.TryParse(Encoding.UTF8.GetString(hex), out var result))
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

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        UInt256 value,
        JsonSerializerOptions options)
    {
        if (value.IsZero)
        {
            writer.WriteRawValue("\"0x0\"");
            return;
        }

        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        ByteArrayConverter.Convert(writer, bytes);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalidOperationException()
    {
        throw new InvalidOperationException();
    }
}
