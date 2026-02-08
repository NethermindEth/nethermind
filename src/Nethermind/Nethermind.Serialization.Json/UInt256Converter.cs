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
            writer.WriteStringValue(usedConversion == NumberConversion.ZeroPaddedHex
                ? "0x0000000000000000000000000000000000000000000000000000000000000000"u8
                : "0x0"u8);
            return;
        }
        switch (usedConversion)
        {
            case NumberConversion.Hex:
                WriteUInt256HexDirect(writer, value);
                break;
            case NumberConversion.Decimal:
                writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WriteStringValue(((BigInteger)value).ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.ZeroPaddedHex:
                {
                    // Fixed 66-byte output: "0x" + 64 hex chars
                    Span<byte> hex = stackalloc byte[66];
                    hex[0] = (byte)'0';
                    hex[1] = (byte)'x';
                    Span<byte> bytes = stackalloc byte[32];
                    value.ToBigEndian(bytes);
                    bytes.OutputBytesToByteHex(hex[2..], extraNibble: false);
                    writer.WriteStringValue(hex);
                }
                break;
            default:
                throw new NotSupportedException($"{usedConversion} format is not supported for {nameof(UInt256)}");
        }
    }

    [SkipLocalsInit]
    private static void WriteUInt256HexDirect(Utf8JsonWriter writer, UInt256 value)
    {
        // Determine significant nibbles using LZCNT on limbs (big-endian: u3 is most significant)
        int leadingZeroBits;
        if (value.u3 != 0)
            leadingZeroBits = BitOperations.LeadingZeroCount(value.u3);
        else if (value.u2 != 0)
            leadingZeroBits = 64 + BitOperations.LeadingZeroCount(value.u2);
        else if (value.u1 != 0)
            leadingZeroBits = 128 + BitOperations.LeadingZeroCount(value.u1);
        else
            leadingZeroBits = 192 + BitOperations.LeadingZeroCount(value.u0);

        int significantNibbles = (256 - leadingZeroBits + 3) >> 2; // ceil(significantBits / 4)
        if (significantNibbles == 0) significantNibbles = 1;

        int totalLen = 2 + significantNibbles; // "0x" + nibbles
        Span<byte> buf = stackalloc byte[66]; // max "0x" + 64
        buf[0] = (byte)'0';
        buf[1] = (byte)'x';

        // Write nibbles from least significant to most significant
        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        int byteOffset = 32 - ((significantNibbles + 1) >> 1);
        ReadOnlySpan<byte> significant = bytes[byteOffset..];
        bool extraNibble = (significantNibbles & 1) != 0;
        significant.OutputBytesToByteHex(buf[2..totalLen], extraNibble: extraNibble);

        writer.WriteStringValue(buf[..totalLen]);
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
