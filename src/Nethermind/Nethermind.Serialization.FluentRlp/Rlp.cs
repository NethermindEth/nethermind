// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Entry points to encode and decode a whole RLP item.</summary>
public static class Rlp
{
    /// <summary>Encodes the items written by <paramref name="action"/>.</summary>
    /// <returns>A new array of the exact encoded length.</returns>
    /// <remarks><paramref name="action"/> runs once.</remarks>
    public static byte[] Write(RefRlpWriterAction action)
        => Write(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

    /// <inheritdoc cref="Write(RefRlpWriterAction)"/>
    public static byte[] Write<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        RlpWriter writer = RlpWriter.BufferingWriter();
        action(ref writer, ctx);

        // CopyBufferedTo writes every byte or throws.
        byte[] result = GC.AllocateUninitializedArray<byte>(writer.Length);
        writer.CopyBufferedTo(result);
        writer.ReturnBuffers();
        return result;
    }

    /// <summary>Encodes the items written by <paramref name="action"/> to <paramref name="output"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    /// <remarks><paramref name="action"/> runs once.</remarks>
    public static int Write<TContext>(IBufferWriter<byte> output, TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        RlpWriter writer = RlpWriter.BufferingWriter();
        action(ref writer, ctx);

        int length = writer.Length;
        writer.CopyBufferedTo(output.GetSpan(length)[..length]);
        writer.ReturnBuffers();
        output.Advance(length);
        return length;
    }

    /// <summary>Encodes <paramref name="value"/> with a converter that knows its length, in one pass and without a buffer copy.</summary>
    /// <returns>A new array of the exact encoded length.</returns>
    /// <exception cref="InvalidOperationException">The converter writes fewer bytes than it computes, or does not end a sequence.</exception>
    /// <exception cref="ArgumentException">The converter writes more bytes than it computes.</exception>
    public static byte[] Write<TConverter, T>(T value) where TConverter : IRlpLengthConverter<T>
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(TConverter.GetLength(value));
        WriteExact<TConverter, T>(result, value);
        return result;
    }

    /// <inheritdoc cref="Write{TConverter, T}(T)"/>
    /// <returns>The number of bytes written to <paramref name="output"/>.</returns>
    public static int Write<TConverter, T>(IBufferWriter<byte> output, T value) where TConverter : IRlpLengthConverter<T>
    {
        int length = TConverter.GetLength(value);
        WriteExact<TConverter, T>(output.GetSpan(length)[..length], value);
        output.Advance(length);
        return length;
    }

    // The converter must fill all of the destination, so that no byte of an uninitialized array is left.
    private static void WriteExact<TConverter, T>(Span<byte> destination, T value) where TConverter : IRlpLengthConverter<T>
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        TConverter.Write(ref writer, value);
        if (writer.Length != destination.Length || writer.HasOpenSequences) throw new InvalidOperationException("RLP converter wrote another length than it computed, or did not end a sequence");
    }

    /// <summary>Gets the encoded length of a byte string.</summary>
    public static int LengthOf(ReadOnlySpan<byte> value)
        => value.Length == 1 && value[0] < RlpPrefix.ShortString ? 1 : LengthOfString(value.Length);

    /// <summary>Gets the encoded length of a non-negative integer.</summary>
    public static unsafe int LengthOf<T>(T value) where T : unmanaged, IBinaryInteger<T>
    {
        int byteCount = (int.CreateTruncating(T.Log2(value)) + 8) / 8;
        // Saturating, as 0x80 does not fit a signed byte: truncated, it would be -128.
        return value <= T.CreateSaturating(RlpPrefix.ShortString - 1) ? 1 : LengthOfString(byteCount);
    }

    /// <summary>Gets the encoded length of a byte string of <paramref name="length"/> bytes, other than a single byte below <c>0x80</c>.</summary>
    public static int LengthOfString(int length) => LengthOfPrefixed(length);

    /// <summary>Gets the encoded length of a sequence whose payload has <paramref name="payloadLength"/> bytes.</summary>
    public static int LengthOfSequence(int payloadLength) => LengthOfPrefixed(payloadLength);

    private static int LengthOfPrefixed(int length)
        => length + (length <= RlpPrefix.MaxShortLength ? 1 : length < 0x100 ? 2 : length < 0x10000 ? 3 : length < 0x1000000 ? 4 : 5);

    /// <summary>Decodes <paramref name="source"/> with <paramref name="func"/>, which must consume all of it.</summary>
    /// <exception cref="RlpReaderException">The input is malformed or has trailing bytes.</exception>
    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        RlpReader reader = new(source);
        T result = func(ref reader);
        if (reader.HasNext) throw new RlpReaderException("RLP has trailing bytes");
        return result;
    }
}
