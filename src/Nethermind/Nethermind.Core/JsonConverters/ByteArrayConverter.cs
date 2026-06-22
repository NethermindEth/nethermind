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
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class ByteArrayConverter : JsonConverter<byte[]>
{
    // '0' = 0x30, 'x' = 0x78, little-endian: 0x7830
    private const ushort HexPrefix = 0x7830;
    private const int InlineHexBufferLength = 256;
    private const int MediumInlineHexBufferLength = 2048;

    public override byte[]? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => Convert(ref reader);

    /// <summary>Reads a QUANTITY-style hex string (EIP-1474)</summary>
    public static byte[]? Convert(ref Utf8JsonReader reader, bool strictHexFormat = false)
        => ConvertCore(ref reader, strictHexFormat, requireEvenLength: false);

    /// <summary>Reads a DATA-style hex string (EIP-1474)</summary>
    public static byte[]? ConvertData(ref Utf8JsonReader reader, bool strictHexFormat = false)
        => ConvertCore(ref reader, strictHexFormat, requireEvenLength: true);

    [SkipLocalsInit]
    private static byte[]? ConvertCore(ref Utf8JsonReader reader, bool strictHexFormat, bool requireEvenLength)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            return null;
        else if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
            ThrowInvalidOperationException();

        if (reader.HasValueSequence)
        {
            return ConvertValueSequence(ref reader, strictHexFormat, requireEvenLength);
        }

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        int length = hex.Length;
        if (length == 0) return null;
        return Bytes.FromUtf8HexString(GetHexValueSpan(hex, strictHexFormat, requireEvenLength));
    }

    [SkipLocalsInit]
    public static bool TryConvertToExactLength(ref Utf8JsonReader reader, scoped Span<byte> destination, bool strictHexFormat = false) =>
        TryConvertToSpan(ref reader, destination, out int bytesWritten, strictHexFormat) &&
        bytesWritten == destination.Length;

    [SkipLocalsInit]
    public static bool TryConvertToSpan(
        ref Utf8JsonReader reader,
        scoped Span<byte> destination,
        out int bytesWritten,
        bool strictHexFormat = false)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
        {
            bytesWritten = 0;
            return false;
        }

        if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
        {
            ThrowInvalidOperationException();
        }

        if (reader.HasValueSequence)
        {
            bytesWritten = 0;
            return false;
        }

        return TryConvertValueSpanToSpan(ref reader, destination, out bytesWritten, strictHexFormat);
    }

    private static bool TryConvertValueSpanToSpan(
        ref Utf8JsonReader reader,
        scoped Span<byte> destination,
        out int bytesWritten,
        bool strictHexFormat)
    {
        ReadOnlySpan<byte> hex = reader.ValueSpan;
        int length = hex.Length;
        if (length == 0)
        {
            bytesWritten = 0;
            return false;
        }

        hex = GetHexValueSpan(hex, strictHexFormat, requireEvenLength: true);

        bytesWritten = (hex.Length >> 1) + (hex.Length & 1);
        if (bytesWritten > destination.Length)
        {
            return false;
        }

        Bytes.FromUtf8HexString(hex, destination[..bytesWritten]);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetHexValueSpan(ReadOnlySpan<byte> hex, bool strictHexFormat, bool requireEvenLength)
    {
        ref byte hexRef = ref MemoryMarshal.GetReference(hex);
        if (hex.Length >= 2 && Unsafe.As<byte, ushort>(ref hexRef) == HexPrefix)
        {
            hex = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref hexRef, 2), hex.Length - 2);
        }
        else if (strictHexFormat)
        {
            throw new SafePublicMessageFormatException(Bytes.ErrMissingPrefix);
        }

        if (requireEvenLength && hex.Length % 2 != 0)
        {
            Bytes.ThrowFormatException(Bytes.ErrOddLength);
        }

        return hex;
    }

    private enum SequenceValueKind { Null, Empty, Bytes }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SequenceValueKind PrepareValueSequence(ref Utf8JsonReader reader, bool strictHexFormat, bool requireEvenLength, out SequenceReader<byte> sr, out int odd, out int outLen)
    {
        ReadOnlySequence<byte> valueSequence = reader.ValueSequence;
        int length = checked((int)valueSequence.Length);
        sr = new SequenceReader<byte>(valueSequence);
        odd = 0;
        outLen = 0;
        if (length == 0) return SequenceValueKind.Null;

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
                        throw new SafePublicMessageFormatException(Bytes.ErrMissingPrefix);
                }
            }
            else if (strictHexFormat)
            {
                throw new SafePublicMessageFormatException(Bytes.ErrMissingPrefix);
            }
        }

        long totalHexChars = length - (hadPrefix ? 2 : 0);
        if (totalHexChars <= 0) return SequenceValueKind.Empty;

        if (requireEvenLength && (totalHexChars & 1) != 0)
            Bytes.ThrowFormatException(Bytes.ErrOddLength);

        odd = (int)(totalHexChars & 1);
        outLen = (int)(totalHexChars >> 1) + odd;
        return SequenceValueKind.Bytes;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DecodeValueSequence(ref SequenceReader<byte> sr, int odd, Span<byte> dest)
    {
        ref byte resultRef = ref MemoryMarshal.GetReference(dest);
        int outPos = 0;

        if (odd == 1)
        {
            if (!sr.TryRead(out byte firstNibble))
                ThrowInvalidOperationException();

            firstNibble = (byte)HexConverter.FromLowerChar(firstNibble | 0x20);
            if (firstNibble > 0x0F)
                Bytes.ThrowFormatException();

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

        if (outPos != dest.Length)
            ThrowInvalidOperationException();
    }

    private static byte[]? ConvertValueSequence(ref Utf8JsonReader reader, bool strictHexFormat, bool requireEvenLength = false)
    {
        switch (PrepareValueSequence(ref reader, strictHexFormat, requireEvenLength, out SequenceReader<byte> sr, out int odd, out int outLen))
        {
            case SequenceValueKind.Null:
                return null;
            case SequenceValueKind.Empty:
                return [];
            default:
                byte[] result = GC.AllocateUninitializedArray<byte>(outLen);
                DecodeValueSequence(ref sr, odd, result);
                return result;
        }
    }

    /// <summary>
    /// Reads a hex string directly into a pool-rented <see cref="ArrayPoolList{T}"/> of bytes, with no
    /// intermediate <c>byte[]</c>. Ownership transfers to the caller, which MUST dispose the result.
    /// Returns <c>null</c> for JSON null or an empty string.
    /// </summary>
    [SkipLocalsInit]
    public static ArrayPoolList<byte>? ConvertToArrayPoolList(ref Utf8JsonReader reader)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            return null;
        if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
            ThrowInvalidOperationException();

        // Value spanning multiple buffer segments: decode straight into the pooled list.
        if (reader.HasValueSequence)
        {
            switch (PrepareValueSequence(ref reader, strictHexFormat: false, requireEvenLength: false, out SequenceReader<byte> sr, out int odd, out int seqLen))
            {
                case SequenceValueKind.Null:
                    return null;
                case SequenceValueKind.Empty:
                    return new ArrayPoolList<byte>(0, 0);
                default:
                    ArrayPoolList<byte> sequenceResult = new(seqLen, seqLen);
                    DecodeValueSequence(ref sr, odd, sequenceResult.AsSpan());
                    return sequenceResult;
            }
        }

        ReadOnlySpan<byte> raw = reader.ValueSpan;
        if (raw.Length == 0) return null;

        ReadOnlySpan<byte> hex = GetHexValueSpan(raw, strictHexFormat: false, requireEvenLength: false);
        int byteLength = (hex.Length >> 1) + (hex.Length & 1);
        ArrayPoolList<byte> result = new(byteLength, byteLength);
        if (byteLength != 0)
        {
            Bytes.FromUtf8HexString(hex, result.AsSpan());
        }
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

        Bytes.FromUtf8HexString(GetHexValueSpan(reader.ValueSpan, strictHexFormat: false, requireEvenLength: false), span);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidOperationException() => throw new InvalidOperationException();

    public override void Write(
        Utf8JsonWriter writer,
        byte[] bytes,
        JsonSerializerOptions options) => Convert(writer, bytes, skipLeadingZeros: false);

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

        Unsafe.SkipInit(out HexBuffer256 buffer);
        if (rawLength <= InlineHexBufferLength)
        {
            WriteHexStringValue(
                writer,
                bytes,
                leadingNibbleZeros,
                addHexPrefix,
                MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer256, byte>(ref buffer), rawLength));
            return;
        }

        if (rawLength <= MediumInlineHexBufferLength)
        {
            WriteHexStringValueWithMediumInlineBuffer(writer, bytes, leadingNibbleZeros, rawLength, addHexPrefix);
            return;
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(rawLength);
        WriteHexStringValue(writer, bytes, leadingNibbleZeros, addHexPrefix, array.AsSpan(0, rawLength));
        ArrayPool<byte>.Shared.Return(array);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteHexStringValueWithMediumInlineBuffer(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        int leadingNibbleZeros,
        int rawLength,
        bool addHexPrefix)
    {
        Unsafe.SkipInit(out HexBuffer2048 buffer);
        WriteHexStringValue(
            writer,
            bytes,
            leadingNibbleZeros,
            addHexPrefix,
            MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer2048, byte>(ref buffer), rawLength));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHexStringValue(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        int leadingNibbleZeros,
        bool addHexPrefix,
        Span<byte> hex)
    {
        int rawLength = hex.Length;
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
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteZeroValue(Utf8JsonWriter writer) => writer.WriteStringValue("0x0"u8);

    [InlineArray(InlineHexBufferLength)]
    private struct HexBuffer256
    {
        private byte _element0;
    }

    [InlineArray(MediumInlineHexBufferLength)]
    private struct HexBuffer2048
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

        Unsafe.SkipInit(out HexBuffer256 buffer);
        if (length <= InlineHexBufferLength)
        {
            WriteHexWithAction(
                writer,
                bytes,
                writeAction,
                leadingNibbleZeros,
                addQuotations,
                addHexPrefix,
                MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer256, byte>(ref buffer), length));
            return;
        }

        if (length <= MediumInlineHexBufferLength)
        {
            WriteHexWithActionWithMediumInlineBuffer(
                writer,
                bytes,
                writeAction,
                leadingNibbleZeros,
                length,
                addQuotations,
                addHexPrefix);
            return;
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(length);
        WriteHexWithAction(
            writer,
            bytes,
            writeAction,
            leadingNibbleZeros,
            addQuotations,
            addHexPrefix,
            array.AsSpan(0, length));
        ArrayPool<byte>.Shared.Return(array);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteHexWithActionWithMediumInlineBuffer(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        WriteHex writeAction,
        int leadingNibbleZeros,
        int length,
        bool addQuotations,
        bool addHexPrefix)
    {
        Unsafe.SkipInit(out HexBuffer2048 buffer);
        WriteHexWithAction(
            writer,
            bytes,
            writeAction,
            leadingNibbleZeros,
            addQuotations,
            addHexPrefix,
            MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer2048, byte>(ref buffer), length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHexWithAction(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        WriteHex writeAction,
        int leadingNibbleZeros,
        bool addQuotations,
        bool addHexPrefix,
        Span<byte> hex)
    {
        int length = hex.Length;
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

    public override void WriteAsPropertyName(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => Convert(writer, value, static (w, h) => w.WritePropertyName(h), skipLeadingZeros: false, addQuotations: false, addHexPrefix: true);
}

/// <summary>Strict byte-array converter for RPC transaction fields that require a <c>0x</c> prefix and even hex digit count.</summary>
public class StrictHexByteArrayConverter : JsonConverter<byte[]>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return ByteArrayConverter.ConvertData(ref reader, strictHexFormat: true);
        }
        catch (FormatException e)
        {
            throw new JsonException(e.Message, e);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        => ByteArrayConverter.Convert(writer, value, skipLeadingZeros: false);
}
