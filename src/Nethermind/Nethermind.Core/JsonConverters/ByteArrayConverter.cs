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

    [SkipLocalsInit]
    public static void Convert(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, bool skipLeadingZeros = true, bool addHexPrefix = true)
    {
        Convert(writer,
            bytes,
            static (w, h) => w.WriteRawValue(h, skipInputValidation: true), skipLeadingZeros, addHexPrefix: addHexPrefix);
    }

    public delegate void WriteHex(Utf8JsonWriter writer, ReadOnlySpan<byte> hex);

    [SkipLocalsInit]
    public static void Convert(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        WriteHex writeAction,
        bool skipLeadingZeros = true,
        bool addQuotations = true,
        bool addHexPrefix = true)
    {
        const int maxStackLength = 128;
        const int stackLength = 256;

        var leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
        var nibblesCount = bytes.Length * 2;

        if (skipLeadingZeros && nibblesCount is not 0 && leadingNibbleZeros == nibblesCount)
        {
            writer.WriteStringValue(Bytes.ZeroHexValue);
            return;
        }

        var prefixLength = addHexPrefix ? 2 : 0;
        var length = nibblesCount - leadingNibbleZeros + prefixLength + (addQuotations ? 2 : 0);

        byte[]? array = null;
        if (length > maxStackLength)
            array = ArrayPool<byte>.Shared.Rent(length);

        Span<byte> hex = (array ?? stackalloc byte[stackLength])[..length];
        var start = 0;
        Index end = ^0;
        if (addQuotations)
        {
            end = ^1;
            hex[^1] = (byte)'"';
            hex[start++] = (byte)'"';
        }

        if (addHexPrefix)
        {
            hex[start++] = (byte)'0';
            hex[start++] = (byte)'x';
        }

        Span<byte> output = hex[start..end];

        ReadOnlySpan<byte> input = bytes[(leadingNibbleZeros / 2)..];
        input.OutputBytesToByteHex(output, extraNibble: (leadingNibbleZeros & 1) != 0);
        writeAction(writer, hex);

        if (array is not null)
            ArrayPool<byte>.Shared.Return(array);
    }

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
