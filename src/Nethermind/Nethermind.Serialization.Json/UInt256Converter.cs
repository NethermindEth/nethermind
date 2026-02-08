// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
                WriteUInt256ZeroPaddedHex(writer, value);
                break;
            default:
                throw new NotSupportedException($"{usedConversion} format is not supported for {nameof(UInt256)}");
        }
    }

    /// <summary>
    /// SIMD-accelerated hex encoding for UInt256 with leading-zero stripping.
    /// Converts limbs directly to hex via PSHUFB — no ToBigEndian materialisation.
    /// Uses WriteRawValue to bypass the JSON encoder entirely.
    /// </summary>
    [SkipLocalsInit]
    private static void WriteUInt256HexDirect(Utf8JsonWriter writer, UInt256 value)
    {
        // Raw JSON: '"' + "0x" + up to 64 hex chars + '"' = 68 bytes max
        Span<byte> buf = stackalloc byte[68];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        if (Ssse3.IsSupported)
        {
            EncodeUInt256HexSsse3(ref Unsafe.Add(ref b, 3), value);
        }
        else
        {
            EncodeUInt256HexScalar(ref Unsafe.Add(ref b, 3), value);
        }

        // Determine significant nibbles using LZCNT on limbs (u3 is most significant)
        int leadingZeroBits;
        if (value.u3 != 0)
            leadingZeroBits = BitOperations.LeadingZeroCount(value.u3);
        else if (value.u2 != 0)
            leadingZeroBits = 64 + BitOperations.LeadingZeroCount(value.u2);
        else if (value.u1 != 0)
            leadingZeroBits = 128 + BitOperations.LeadingZeroCount(value.u1);
        else
            leadingZeroBits = 192 + BitOperations.LeadingZeroCount(value.u0);

        int nibbleCount = (259 - leadingZeroBits) >> 2; // ceil(significantBits / 4)
        if (nibbleCount == 0) nibbleCount = 1;

        int start = 67 - nibbleCount;

        Unsafe.Add(ref b, start - 3) = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, start - 2), (ushort)0x7830); // "0x" LE
        Unsafe.Add(ref b, 67) = (byte)'"';

        writer.WriteRawValue(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref b, start - 3), nibbleCount + 4),
            skipInputValidation: true);
    }

    /// <summary>
    /// SIMD-accelerated hex encoding for UInt256 with full zero-padding (always 64 hex chars).
    /// </summary>
    [SkipLocalsInit]
    private static void WriteUInt256ZeroPaddedHex(Utf8JsonWriter writer, UInt256 value)
    {
        // Raw JSON: '"' + "0x" + 64 hex chars + '"' = 68 bytes
        Span<byte> buf = stackalloc byte[68];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        Unsafe.Add(ref b, 0) = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 1), (ushort)0x7830); // "0x" LE

        if (Ssse3.IsSupported)
        {
            EncodeUInt256HexSsse3(ref Unsafe.Add(ref b, 3), value);
        }
        else
        {
            EncodeUInt256HexScalar(ref Unsafe.Add(ref b, 3), value);
        }

        Unsafe.Add(ref b, 67) = (byte)'"';

        writer.WriteRawValue(buf, skipInputValidation: true);
    }

    /// <summary>
    /// Encodes all 4 UInt256 limbs to 64 hex chars using SSSE3 PSHUFB nibble lookup.
    /// Converts limbs directly — no intermediate ToBigEndian byte array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeUInt256HexSsse3(ref byte dest, UInt256 value)
    {
        Vector128<byte> hexLookup = Vector128.Create(
            (byte)'0', (byte)'1', (byte)'2', (byte)'3',
            (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            (byte)'8', (byte)'9', (byte)'a', (byte)'b',
            (byte)'c', (byte)'d', (byte)'e', (byte)'f');
        Vector128<byte> mask = Vector128.Create((byte)0x0F);

        // Pack limbs big-endian: [u3_BE, u2_BE] = bytes 0-15 (most significant first)
        Vector128<byte> chunk0 = Vector128.Create(
            BinaryPrimitives.ReverseEndianness(value.u3),
            BinaryPrimitives.ReverseEndianness(value.u2)).AsByte();

        Vector128<byte> hi0 = Sse2.ShiftRightLogical(chunk0.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo0 = chunk0 & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi0, lo0)).StoreUnsafe(ref dest);
        Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi0, lo0)).StoreUnsafe(ref Unsafe.Add(ref dest, 16));

        // Pack limbs big-endian: [u1_BE, u0_BE] = bytes 16-31
        Vector128<byte> chunk1 = Vector128.Create(
            BinaryPrimitives.ReverseEndianness(value.u1),
            BinaryPrimitives.ReverseEndianness(value.u0)).AsByte();

        Vector128<byte> hi1 = Sse2.ShiftRightLogical(chunk1.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo1 = chunk1 & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi1, lo1)).StoreUnsafe(ref Unsafe.Add(ref dest, 32));
        Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi1, lo1)).StoreUnsafe(ref Unsafe.Add(ref dest, 48));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeUInt256HexScalar(ref byte dest, UInt256 value)
    {
        WriteLimbHex(ref dest, value.u3);
        WriteLimbHex(ref Unsafe.Add(ref dest, 16), value.u2);
        WriteLimbHex(ref Unsafe.Add(ref dest, 32), value.u1);
        WriteLimbHex(ref Unsafe.Add(ref dest, 48), value.u0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLimbHex(ref byte dest, ulong limb)
    {
        ulong be = BinaryPrimitives.ReverseEndianness(limb);
        for (int i = 0; i < 8; i++)
        {
            int byteVal = (int)(be >> (i << 3)) & 0xFF;
            int hi = byteVal >> 4;
            int lo = byteVal & 0xF;
            Unsafe.Add(ref dest, i * 2) = (byte)(hi + 48 + (((9 - hi) >> 31) & 39));
            Unsafe.Add(ref dest, i * 2 + 1) = (byte)(lo + 48 + (((9 - lo) >> 31) & 39));
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

    [SkipLocalsInit]
    private static void WriteHexPropertyName(Utf8JsonWriter writer, UInt256 value, bool isZeroPadded)
    {
        // "0x" + 64 hex chars = 66 bytes max
        Span<byte> buf = stackalloc byte[66];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        if (Ssse3.IsSupported)
        {
            EncodeUInt256HexSsse3(ref Unsafe.Add(ref b, 2), value);
        }
        else
        {
            EncodeUInt256HexScalar(ref Unsafe.Add(ref b, 2), value);
        }

        if (isZeroPadded)
        {
            Unsafe.WriteUnaligned(ref b, (ushort)0x7830); // "0x" LE
            writer.WritePropertyName(buf);
        }
        else
        {
            int leadingZeroBits;
            if (value.u3 != 0)
                leadingZeroBits = BitOperations.LeadingZeroCount(value.u3);
            else if (value.u2 != 0)
                leadingZeroBits = 64 + BitOperations.LeadingZeroCount(value.u2);
            else if (value.u1 != 0)
                leadingZeroBits = 128 + BitOperations.LeadingZeroCount(value.u1);
            else
                leadingZeroBits = 192 + BitOperations.LeadingZeroCount(value.u0);

            int nibbleCount = (259 - leadingZeroBits) >> 2;
            if (nibbleCount == 0) nibbleCount = 1;

            int start = 66 - nibbleCount;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, start - 2), (ushort)0x7830); // "0x" LE
            writer.WritePropertyName(buf[(start - 2)..]);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowJsonException()
    {
        throw new JsonException();
    }
}
