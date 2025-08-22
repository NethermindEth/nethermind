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
    private static readonly ushort _hexPrefix = MemoryMarshal.Cast<byte, ushort>("0x"u8)[0];

    public override byte[]? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return Convert(ref reader);
    }

    public static byte[]? Convert(ref Utf8JsonReader reader)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            return null;
        else if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
            ThrowInvalidOperationException();

        if (reader.HasValueSequence)
        {
            return ConvertValueSequence(ref reader);
        }

        int length = reader.ValueSpan.Length;
        ReadOnlySpan<byte> hex = reader.ValueSpan;
        if (hex.Length == 0) return null;
        if (length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == _hexPrefix)
            hex = hex[2..];

        return Bytes.FromUtf8HexString(hex);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[]? ConvertValueSequence(ref Utf8JsonReader reader)
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
                if (sr.TryPeek(out byte b1) && (b1 == (byte)'x'))
                {
                    sr.Advance(1);
                    hadPrefix = true;
                }
                else
                {
                    // rewind if not really a prefix
                    sr.Rewind(1);
                }
            }
        }

        // Compute total hex digit count (after prefix)
        long totalHexChars = length - (hadPrefix ? 2 : 0);
        if (totalHexChars <= 0) return [];

        int odd = (int)(totalHexChars & 1);
        int outLenFinal = (int)(totalHexChars >> 1) + odd;
        if (outLenFinal == 0) return [];

        byte[] result = GC.AllocateUninitializedArray<byte>(outLenFinal);
        Span<byte> output = result;
        if (odd == 1)
        {
            // If odd, we deal with the extra nibble, so we are left with an even number of nibbles
            if (!sr.TryRead(out byte firstNibble))
            {
                ThrowInvalidOperationException();
            }
            firstNibble = (byte)HexConverter.FromLowerChar(firstNibble | 0x20);
            if (firstNibble > 0x0F)
            {
                ThrowFormatException();
            }
            result[0] = firstNibble;
            output = output[1..];
        }

        // Stackalloc outside of the loop to avoid stackoverflow.
        Span<byte> twoNibbles = stackalloc byte[2];
        while (!sr.End)
        {
            ReadOnlySpan<byte> first = sr.UnreadSpan;
            if (!first.IsEmpty)
            {
                // Decode the largest even-length slice of the current contiguous span without copying.
                int evenLen = first.Length & ~1; // largest even
                if (evenLen > 0)
                {
                    int outBytes = evenLen >> 1;
                    Bytes.FromUtf8HexString(first.Slice(0, evenLen), output.Slice(0, outBytes));
                    output = output.Slice(outBytes);
                    sr.Advance(evenLen);
                    continue;
                }
            }

            // Either current span is empty or has exactly 1 trailing nibble; marshal an even-sized chunk.
            long remaining = sr.Remaining;
            if (remaining == 0) break;

            // If remaining is even overall, remaining will be >= 2 here; be defensive just in case.
            if (remaining == 1)
            {
                ThrowInvalidOperationException();
            }

            if (!sr.TryCopyTo(twoNibbles))
            {
                // Should not happen since CopyTo should copy 2 hex chars and bridge the spans.
                ThrowInvalidOperationException();
            }

            Bytes.FromUtf8HexString(twoNibbles, output[..1]);
            output = output[1..];
            sr.Advance(twoNibbles.Length);
        }

        if (!output.IsEmpty)
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
        if (hex.Length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == _hexPrefix)
        {
            hex = hex[2..];
        }

        Bytes.FromUtf8HexString(hex, span);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowFormatException() => throw new FormatException();

    [DoesNotReturn, StackTraceHidden]
    private static Exception ThrowInvalidOperationException() => throw new InvalidOperationException();

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
        return Convert(ref reader) ?? throw ThrowInvalidOperationException();
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        Convert(writer, value, static (w, h) => w.WritePropertyName(h), skipLeadingZeros: false, addQuotations: false, addHexPrefix: true);
    }
}
