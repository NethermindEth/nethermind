// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
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
        JsonSerializerOptions options) =>
        ReadInternal(ref reader, JsonTokenType.String);

    // length of UIn256.MaxValue decimal string "115792089237316195423570985008687907853269984665640564039457584007913129639935"
    const int maxLength = 78;

    [SkipLocalsInit]
    private static UInt256 ReadInternal(ref Utf8JsonReader reader, JsonTokenType allowedTokenType)
    {
        int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;

        if (length is 0 or > maxLength)
        {
            ThrowJsonException();
        }

        if (reader.HasValueSequence)
        {
            Span<byte> span = stackalloc byte[length];
            reader.ValueSequence.CopyTo(span);
            return ReadNumber(reader, allowedTokenType, span);
        }

        return ReadNumber(reader, allowedTokenType, reader.ValueSpan);
    }

    [SkipLocalsInit]
    private static UInt256 ReadNumber(Utf8JsonReader reader, JsonTokenType allowedTokenType, ReadOnlySpan<byte> span)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (Utf8Parser.TryParse(span, out ulong shortValue, out int bytesConsumed) && span.Length == bytesConsumed)
            {
                return new UInt256(shortValue);
            }

            Span<char> chars = stackalloc char[span.Length + 1];
            chars[span.Length] = '\0';
            Encoding.UTF8.GetChars(span, chars);

            return UInt256.Parse(chars, NumberStyles.None);
        }

        if (reader.TokenType != allowedTokenType)
        {
            ThrowJsonException();
        }

        return ReadHex(span);
    }

    public static UInt256 ReadHex(ReadOnlySpan<byte> hex)
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

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        UInt256 value,
        JsonSerializerOptions options)
    {
        NumberConversion usedConversion = ForcedNumberConversion.GetFinalConversion();
        if (value.IsZero)
        {
            writer.WriteRawValue(usedConversion == NumberConversion.ZeroPaddedHex
                ? "\"0x0000000000000000000000000000000000000000000000000000000000000000\""u8
                : "\"0x0\""u8);
            return;
        }
        switch (usedConversion)
        {
            case NumberConversion.Hex:
                {
                    Span<byte> bytes = stackalloc byte[32];
                    value.ToBigEndian(bytes);
                    ByteArrayConverter.Convert(writer, bytes);
                }
                break;
            case NumberConversion.Decimal:
                writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WriteStringValue(((BigInteger)value).ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.ZeroPaddedHex:
                {
                    Span<byte> bytes = stackalloc byte[32];
                    value.ToBigEndian(bytes);
                    ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
                }
                break;
            default:
                throw new NotSupportedException($"{usedConversion} format is not supported for {nameof(UInt256)}");
        }
    }

    public override UInt256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadInternal(ref reader, JsonTokenType.PropertyName);

    [SkipLocalsInit]
    public override void WriteAsPropertyName(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options)
    {
        NumberConversion usedConversion = ForcedNumberConversion.GetFinalConversion();
        if (value.IsZero)
        {
            writer.WritePropertyName(usedConversion == NumberConversion.ZeroPaddedHex
                ? "0x0000000000000000000000000000000000000000000000000000000000000000"u8
                : "0x0"u8);
            return;
        }
        switch (usedConversion)
        {
            case NumberConversion.Hex:
                WriteHexPropertyName(writer, value, false);
                break;
            case NumberConversion.Decimal:
                writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WritePropertyName(((BigInteger)value).ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.ZeroPaddedHex:
                WriteHexPropertyName(writer, value, true);
                break;
            default:
                throw new NotSupportedException($"{usedConversion} format is not supported for {nameof(UInt256)}");
        }
    }

    private static void WriteHexPropertyName(Utf8JsonWriter writer, UInt256 value, bool isZeroPadded)
    {
        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        ByteArrayConverter.Convert(
            writer,
            bytes,
            static (w, h) => w.WritePropertyName(h),
            skipLeadingZeros: !isZeroPadded,
            addQuotations: false);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowJsonException()
    {
        throw new JsonException();
    }
}
