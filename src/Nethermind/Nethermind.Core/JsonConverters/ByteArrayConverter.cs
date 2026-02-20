// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class ByteArrayConverter : JsonConverter<byte[]>
{
    // '0' = 0x30, 'x' = 0x78, little-endian: 0x7830
    private const ushort HexPrefix = 0x7830;

    public override byte[]? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return Convert(ref reader);
    }

    [SkipLocalsInit]
    public static byte[]? Convert(ref Utf8JsonReader reader, bool strictHexFormat = false)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            return null;
        else if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
            ThrowInvalidOperationException();

        if (reader.HasValueSequence)
        {
            return ConvertValueSequence(ref reader, strictHexFormat);
        }

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        int length = hex.Length;
        if (length == 0) return null;
        ref byte hexRef = ref MemoryMarshal.GetReference(hex);
        if (length >= 2 && Unsafe.As<byte, ushort>(ref hexRef) == HexPrefix)
        {
            hex = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref hexRef, 2), length - 2);
        }
        else if (strictHexFormat) ThrowFormatException();

        return Bytes.FromUtf8HexString(hex);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[]? ConvertValueSequence(ref Utf8JsonReader reader, bool strictHexFormat)
    {
        ReadOnlySequence<byte> valueSequence = reader.ValueSequence;
        int length = checked((int)valueSequence.Length);
        if (length == 0) return null;

        // Detect and skip 0x prefix even if split across segments
        SequenceReader<byte> sr = new(valueSequence);
        bool hadPrefix = false;
        if (sr.TryPeek(out byte b0))
        {
            if (b0 == (byte)'0')
            {
                sr.Advance(1);
                if (sr.TryPeek(out byte b1) && b1 == (byte)'x')
                {
                    sr.Advance(1);
                    hadPrefix = true;
                }
                else
                {
                    sr.Rewind(1);
                    if (strictHexFormat)
                        ThrowFormatException();
                }
            }
            else if (strictHexFormat)
            {
                ThrowFormatException();
            }
        }

        long totalHexChars = length - (hadPrefix ? 2 : 0);
        if (totalHexChars <= 0) return [];

        int odd = (int)(totalHexChars & 1);
        int outLen = (int)(totalHexChars >> 1) + odd;

        byte[] result = GC.AllocateUninitializedArray<byte>(outLen);
        ref byte resultRef = ref MemoryMarshal.GetArrayDataReference(result);
        int outPos = 0;

        if (odd == 1)
        {
            if (!sr.TryRead(out byte firstNibble))
                ThrowInvalidOperationException();

            firstNibble = (byte)HexConverter.FromLowerChar(firstNibble | 0x20);
            if (firstNibble > 0x0F)
                ThrowFormatException();

            Unsafe.Add(ref resultRef, outPos++) = firstNibble;
        }

        // Use ushort as 2-byte buffer instead of stackalloc
        Unsafe.SkipInit(out ushort twoNibblesStorage);
        Span<byte> twoNibbles = MemoryMarshal.CreateSpan(ref Unsafe.As<ushort, byte>(ref twoNibblesStorage), 2);

        while (!sr.End)
        {
            ReadOnlySpan<byte> span = sr.UnreadSpan;
            if (!span.IsEmpty)
            {
                int evenLen = span.Length & ~1;
                if (evenLen > 0)
                {
                    int outBytes = evenLen >> 1;
                    Bytes.FromUtf8HexString(span.Slice(0, evenLen),
                        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref resultRef, outPos), outBytes));
                    outPos += outBytes;
                    sr.Advance(evenLen);
                    continue;
                }
            }

            long remaining = sr.Remaining;
            if (remaining == 0) break;
            if (remaining == 1)
                ThrowInvalidOperationException();

            if (!sr.TryCopyTo(twoNibbles))
                ThrowInvalidOperationException();

            Bytes.FromUtf8HexString(twoNibbles, MemoryMarshal.CreateSpan(ref Unsafe.Add(ref resultRef, outPos), 1));
            outPos++;
            sr.Advance(2);
        }

        if (outPos != outLen)
            ThrowInvalidOperationException();

        return result;
    }

    public static void Convert(ref Utf8JsonReader reader, scoped Span<byte> span)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
        {
            return;
        }

        if (tokenType != JsonTokenType.String)
        {
            ThrowInvalidOperationException();
        }

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        if (hex.Length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == HexPrefix)
        {
            hex = hex[2..];
        }

        Bytes.FromUtf8HexString(hex, span);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowFormatException() => throw new FormatException();

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidOperationException() => throw new InvalidOperationException();

    public override void Write(
        Utf8JsonWriter writer,
        byte[] bytes,
        JsonSerializerOptions options)
    {
        Convert(writer, bytes, skipLeadingZeros: false);
    }

    /// <summary>
    /// Writes bytes as a hex string value (e.g. "0xabcd") using WriteRawValue.
    /// </summary>
    [SkipLocalsInit]
    public static void Convert(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, bool skipLeadingZeros = true, bool addHexPrefix = true)
    {
        int leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
        int nibblesCount = bytes.Length * 2;

        if (skipLeadingZeros && nibblesCount is not 0 && leadingNibbleZeros == nibblesCount)
        {
            WriteZeroValue(writer);
            return;
        }

        int prefixLength = addHexPrefix ? 2 : 0;
        // +2 for surrounding quotes: "0xABCD..."
        int rawLength = nibblesCount - leadingNibbleZeros + prefixLength + 2;

        byte[]? array = null;
        Unsafe.SkipInit(out HexBuffer256 buffer);
        Span<byte> hex = rawLength <= 256
            ? MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer256, byte>(ref buffer), 256)
            : (array = ArrayPool<byte>.Shared.Rent(rawLength));
        hex = hex[..rawLength];

        // Build the JSON string value directly: "0x<hex>"
        ref byte hexRef = ref MemoryMarshal.GetReference(hex);
        hexRef = (byte)'"';
        int start = 1;
        if (addHexPrefix)
        {
            Unsafe.As<byte, ushort>(ref Unsafe.Add(ref hexRef, 1)) = HexPrefix;
            start = 3;
        }
        Unsafe.Add(ref hexRef, rawLength - 1) = (byte)'"';

        int offset = leadingNibbleZeros >>> 1;
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), offset), bytes.Length - offset)
            .OutputBytesToByteHex(
                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref hexRef, start), rawLength - 1 - start),
                extraNibble: (leadingNibbleZeros & 1) != 0);
        // Hex chars (0-9, a-f) never need JSON escaping — bypass encoder entirely
        writer.WriteRawValue(hex, skipInputValidation: true);

        if (array is not null)
            ArrayPool<byte>.Shared.Return(array);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteZeroValue(Utf8JsonWriter writer) => writer.WriteStringValue("0x0"u8);

    [InlineArray(256)]
    private struct HexBuffer256
    {
        private byte _element0;
    }

    public delegate void WriteHex(Utf8JsonWriter writer, ReadOnlySpan<byte> hex);

    /// <summary>
    /// Writes bytes as hex using a custom write action (e.g. for property names).
    /// </summary>
    [SkipLocalsInit]
    public static void Convert(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        WriteHex writeAction,
        bool skipLeadingZeros = true,
        bool addQuotations = true,
        bool addHexPrefix = true)
    {
        int leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
        int nibblesCount = bytes.Length * 2;

        if (skipLeadingZeros && nibblesCount is not 0 && leadingNibbleZeros == nibblesCount)
        {
            WriteZeroValue(writer, writeAction, addQuotations);
            return;
        }

        int prefixLength = addHexPrefix ? 2 : 0;
        int quotesLength = addQuotations ? 2 : 0;
        int length = nibblesCount - leadingNibbleZeros + prefixLength + quotesLength;

        byte[]? array = null;
        Unsafe.SkipInit(out HexBuffer256 buffer);
        Span<byte> hex = length <= 256
            ? MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer256, byte>(ref buffer), 256)
            : (array = ArrayPool<byte>.Shared.Rent(length));
        hex = hex[..length];

        ref byte hexRef = ref MemoryMarshal.GetReference(hex);
        int start = 0;
        int endPad = 0;
        if (addQuotations)
        {
            hexRef = (byte)'"';
            Unsafe.Add(ref hexRef, length - 1) = (byte)'"';
            start = 1;
            endPad = 1;
        }

        if (addHexPrefix)
        {
            Unsafe.As<byte, ushort>(ref Unsafe.Add(ref hexRef, start)) = HexPrefix;
            start += 2;
        }

        ReadOnlySpan<byte> input = bytes[(leadingNibbleZeros >>> 1)..];
        input.OutputBytesToByteHex(
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref hexRef, start), length - start - endPad),
            extraNibble: (leadingNibbleZeros & 1) != 0);
        writeAction(writer, hex);

        if (array is not null)
            ArrayPool<byte>.Shared.Return(array);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteZeroValue(Utf8JsonWriter writer, WriteHex writeAction, bool addQuotations)
        => writeAction(writer, addQuotations ? "\"0x0\""u8 : "0x0"u8);

    public override byte[] ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[]? result = Convert(ref reader);

        if (result is null)
            ThrowInvalidOperationException();

        return result;
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        Convert(writer, value, static (w, h) => w.WritePropertyName(h), skipLeadingZeros: false, addQuotations: false, addHexPrefix: true);
    }
}
