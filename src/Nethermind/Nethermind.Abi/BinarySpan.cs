// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Abi;

/// <summary>Reads from a scoped reader; see <see cref="BinarySpanReader.Scoped{T, TCtx}"/>.</summary>
public delegate T BinarySpanReaderFunc<out T, in TCtx>(TCtx ctx, ref BinarySpanReader r);

/// <summary>Forward cursor over an ABI encoding, passed to <see cref="Abi{T}.Read"/>.</summary>
public ref struct BinarySpanReader
{
    // Renting from the pool costs more than allocating a short array, which also bounds what a malformed input can make it allocate.
    private const int MaxUnpooledDynamicLength = 4;

    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    /// <summary>Number of bytes after the current position.</summary>
    public readonly int Remaining => _span.Length - _position;

    /// <summary>Creates a reader positioned at the start of <paramref name="span"/>.</summary>
    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    /// <summary>Returns the next <paramref name="count"/> bytes and advances past them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than <paramref name="count"/> bytes remain.</exception>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> result = _span.Slice(_position, count);

        _position += count;
        return result;
    }

    /// <summary>Returns the next <paramref name="length"/> bytes and advances past them and their zero padding to a multiple of 32 bytes.</summary>
    /// <exception cref="AbiException">The padding is not zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The input ends before the padding does.</exception>
    public ReadOnlySpan<byte> ReadBytesPadded(int length)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        ReadOnlySpan<byte> padding = ReadBytes(Padding.To32(length) - length);
        if (padding.ContainsAnyExcept((byte)0)) throw new AbiException();

        return bytes;
    }

    internal void Advance(int bytes) => _position += bytes;

    /// <summary>Runs <paramref name="inner"/> on a reader that starts at the current position, then advances past what it read.</summary>
    /// <remarks>Offsets in an ABI encoding are relative to the start of the enclosing tuple or array, which is where the scoped reader starts.</remarks>
    public T Scoped<T, TCtx>(TCtx ctx, BinarySpanReaderFunc<T, TCtx> inner)
    {
        BinarySpanReader reader = new(_span[_position..]);
        T result = inner(ctx, ref reader);
        _position += reader._position;
        return result;
    }

    internal T ReadOffset<T>(AbiReadFunc<T> read, ref int next)
    {
        ReadOnlySpan<byte> offsetBytes = ReadBytes(32);
        int restorePosition = _position;

        UInt256 offset = new(offsetBytes, isBigEndian: true);
        if (offset != (UInt256)next) throw new AbiException();

        _position = next;
        T result = read(ref this);
        next = _position;

        _position = restorePosition;

        return result;
    }

    internal T[] ReadArray<T>(Abi<T> element, int length) =>
        Scoped((element, length), static ((Abi<T>, int) ctx, ref BinarySpanReader r) =>
        {
            (Abi<T> element, int length) = ctx;

            int headSize = checked(length * element.HeadSize);
            if (headSize > r.Remaining) throw new AbiException();
            if (length == 0) return [];

            T[] values;
            int next = headSize;
            if (element.IsDynamic && length > MaxUnpooledDynamicLength)
            {
                // Only the offsets are known to fit the input, and an element can decode to far more memory than its offset word,
                // so the elements go to a pooled buffer that grows as they decode, and only the complete result is allocated.
                using ArrayPoolListRef<T> decoded = new(MaxUnpooledDynamicLength);
                for (int i = 0; i < length; i++)
                {
                    decoded.Add(r.ReadOffset(element.Read, ref next));
                }
                values = decoded.ToArray();
            }
            else if (element.IsDynamic)
            {
                values = new T[length];
                for (int i = 0; i < length; i++)
                {
                    values[i] = r.ReadOffset(element.Read, ref next);
                }
            }
            else
            {
                values = new T[length];
                for (int i = 0; i < length; i++)
                {
                    values[i] = element.Read(ref r);
                }
            }
            r.Advance(next - headSize);

            return values;
        });
}

/// <summary>Writes to a scoped writer; see <see cref="BinarySpanWriter.Scoped{TCtx}"/>.</summary>
public delegate void BinarySpanWriterAction<in TCtx>(TCtx ctx, ref BinarySpanWriter w);

/// <summary>Forward cursor over an encoding buffer, passed to <see cref="Abi{T}.Write"/>.</summary>
/// <remarks>Every byte inside the encoding is written, so the buffer does not need to be cleared first.</remarks>
public ref struct BinarySpanWriter
{
    private readonly Span<byte> _span;
    private int _position;

    /// <summary>Number of bytes before the current position.</summary>
    public readonly int Written => _position;

    /// <summary>Creates a writer positioned at the start of <paramref name="span"/>.</summary>
    public BinarySpanWriter(Span<byte> span)
    {
        _span = span;
        _position = 0;
    }

    /// <summary>Copies <paramref name="bytes"/> and advances past them.</summary>
    /// <exception cref="ArgumentException">Fewer bytes remain than <paramref name="bytes"/> holds.</exception>
    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_span[_position..]);
        _position += bytes.Length;
    }

    /// <summary>Copies <paramref name="bytes"/> and zero-pads them to a multiple of 32 bytes, advancing past both.</summary>
    public void WritePadded(scoped ReadOnlySpan<byte> bytes)
    {
        Write(bytes);

        int padding = Padding.To32(bytes.Length) - bytes.Length;
        Take(padding).Clear();
    }

    internal void Advance(int bytes) => _position += bytes;

    /// <summary>Returns the next <paramref name="size"/> bytes for the caller to fill, and advances past them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than <paramref name="size"/> bytes remain.</exception>
    public Span<byte> Take(int size)
    {
        Span<byte> span = _span[_position..(_position + size)];
        _position += size;
        return span;
    }

    /// <summary>Runs <paramref name="inner"/> on a writer that starts at the current position, then advances past what it wrote.</summary>
    /// <remarks>Offsets in an ABI encoding are relative to the start of the enclosing tuple or array, which is where the scoped writer starts.</remarks>
    public void Scoped<TCtx>(TCtx ctx, BinarySpanWriterAction<TCtx> inner)
    {
        BinarySpanWriter writer = new(_span[_position..]);
        inner(ctx, ref writer);
        _position += writer._position;
    }

    internal void WriteOffset<T>(AbiWriteAction<T> write, T value, ref int next)
    {
        Span<byte> offsetLocation = Take(32);
        // Constant length (rather than `..^sizeof(int)`) lets the JIT unroll the clear.
        offsetLocation[..(32 - sizeof(int))].Clear();
        BinaryPrimitives.WriteInt32BigEndian(offsetLocation[^sizeof(int)..], next);

        int restorePosition = _position;
        _position = next;
        write(ref this, value);
        next = _position;

        _position = restorePosition;
    }

    internal void WriteArray<T>(Abi<T> element, T[] values) =>
        Scoped((element, values), static ((Abi<T>, T[]) ctx, ref BinarySpanWriter w) =>
        {
            (Abi<T> element, T[] values) = ctx;

            int headSize = checked(values.Length * element.HeadSize);
            int next = headSize;
            foreach (T value in values)
            {
                if (element.IsDynamic) w.WriteOffset(element.Write, value, ref next);
                else element.Write(ref w, value);
            }
            w.Advance(next - headSize);
        });
}


internal static class Padding
{
    // Equivalent to:
    // int rem = length % 32;
    // return rem == 0 ? length : length + (32 - rem);
    public static int To32(int length) => checked(length + 31) & ~31;
}
