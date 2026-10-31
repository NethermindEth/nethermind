// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Reads a value from an <see cref="RlpReader"/> with an extra context argument.</summary>
public delegate TResult RefRlpReaderFunc<out TResult, in TContext>(scoped ref RlpReader arg, TContext value)
    where TResult : allows ref struct;

/// <summary>Reads a value from an <see cref="RlpReader"/>.</summary>
public delegate TResult RefRlpReaderFunc<out TResult>(scoped ref RlpReader arg)
    where TResult : allows ref struct;

/// <summary>The exception that is thrown when the input is not a canonical RLP encoding of the expected shape.</summary>
public class RlpReaderException(string message) : Exception(message);

/// <summary>
/// Reads canonical RLP items from a span of bytes.
/// </summary>
/// <remarks>
/// Every malformed, truncated or non-canonical input throws <see cref="RlpReaderException"/>:
/// a single byte below <c>0x80</c> must not have a prefix, long-form prefixes must not encode lengths below 56
/// or have leading zero bytes, and integers must not have leading zero bytes.
/// A reader for a sequence only sees the payload of that sequence, so no item can extend past its parent.
/// </remarks>
public ref struct RlpReader
{
    // The stack check is a runtime call, so it runs once every few nesting levels only.
    private const int StackCheckInterval = 32;

    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _depth;
    private int _position;

    /// <summary>Creates a reader over <paramref name="buffer"/>.</summary>
    public RlpReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    private RlpReader(ReadOnlySpan<byte> buffer, int depth) : this(buffer) => _depth = depth;

    /// <summary>Gets whether unread bytes are left.</summary>
    public readonly bool HasNext => _position < _buffer.Length;

    /// <summary>Gets the number of unread bytes.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Gets the number of bytes read so far.</summary>
    public readonly int BytesRead => _position;

    /// <summary>Counts the items left in this reader without reading them.</summary>
    /// <remarks>
    /// Each item takes at least one byte, so the count is at most the number of unread bytes and is safe to size a
    /// collection with.
    /// </remarks>
    /// <exception cref="RlpReaderException">An item prefix is malformed or truncated.</exception>
    public readonly int CountItems()
    {
        RlpReader reader = this;
        int count = 0;
        while (reader.HasNext)
        {
            int headerLength = reader.ReadHeader(out int length, out _);
            reader._position += headerLength + length;
            count++;
        }

        return count;
    }

    /// <summary>Reads a non-negative integer.</summary>
    /// <exception cref="RlpReaderException">The item is not a canonical integer or does not fit in <typeparamref name="T"/>.</exception>
    public T ReadInteger<T>() where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<byte> bigEndian = ReadIntegerBytes();
        if (bigEndian.Length > Unsafe.SizeOf<T>()) ThrowInvalid("RLP integer is too large");

        if (Unsafe.SizeOf<T>() <= sizeof(ulong))
        {
            ulong value = 0;
            foreach (byte b in bigEndian)
            {
                value = (value << 8) | b;
            }

            // A full-width value of a signed type would come out negative.
            int valueBits = Unsafe.SizeOf<T>() * 8 - (T.IsNegative(T.AllBitsSet) ? 1 : 0);
            if (value >> (valueBits - 1) > 1) ThrowInvalid("RLP integer is too large");
            return T.CreateTruncating(value);
        }

        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
        buffer.Clear();
        bigEndian.CopyTo(buffer[^bigEndian.Length..]);
        if (!T.TryReadBigEndian(buffer, isUnsigned: true, out T result) || T.IsNegative(result))
        {
            ThrowInvalid("RLP integer is too large");
        }

        return result;
    }

    /// <summary>Reads the big-endian bytes of a non-negative integer.</summary>
    /// <returns>The bytes without leading zeros; empty for zero.</returns>
    /// <exception cref="RlpReaderException">The item is not a canonical integer.</exception>
    public ReadOnlySpan<byte> ReadIntegerBytes()
    {
        ReadOnlySpan<byte> bigEndian = ReadBytes();
        if (bigEndian.Length > 0 && bigEndian[0] == 0) ThrowInvalid("RLP integer has leading zero bytes");
        return bigEndian;
    }

    /// <summary>Reads a byte string.</summary>
    /// <returns>The payload of the byte string, without its prefix.</returns>
    /// <exception cref="RlpReaderException">The item is not a canonical byte string.</exception>
    public ReadOnlySpan<byte> ReadBytes()
    {
        ReadOnlySpan<byte> buffer = _buffer;
        int position = _position;
        if ((uint)position >= (uint)buffer.Length) ThrowInvalid("RLP is truncated");

        byte prefix = buffer[position];
        if (prefix < RlpPrefix.ShortString)
        {
            _position = position + 1;
            return buffer.Slice(position, 1);
        }

        int start = position + 1;
        int length = 0;
        if (prefix <= RlpPrefix.LongString)
        {
            length = prefix - RlpPrefix.ShortString;
            if (length == 1 && (uint)start < (uint)buffer.Length && buffer[start] < RlpPrefix.ShortString)
            {
                ThrowInvalid("RLP single byte below 0x80 must not have a prefix");
            }
        }
        else if (prefix < RlpPrefix.ShortList)
        {
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongString);
            start += prefix - RlpPrefix.LongString;
        }
        else
        {
            ThrowInvalid("RLP does not correspond to a byte string");
        }

        if ((long)start + length > buffer.Length) ThrowInvalid("RLP is truncated");
        _position = start + length;
        return buffer.Slice(start, length);
    }

    /// <summary>Reads a byte string of exactly <paramref name="length"/> bytes.</summary>
    /// <exception cref="RlpReaderException">The item is not a canonical byte string of that length.</exception>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        // A string of 2 to 55 bytes has exactly one canonical prefix, so one byte compare checks prefix and length.
        if (length is < 2 or > RlpPrefix.MaxShortLength)
        {
            ReadOnlySpan<byte> bytes = ReadBytes();
            if (bytes.Length != length) ThrowInvalid("RLP byte string has another length than expected");
            return bytes;
        }

        ReadOnlySpan<byte> buffer = _buffer;
        int position = _position;
        if ((long)position + 1 + length > buffer.Length || buffer[position] != RlpPrefix.ShortString + length)
        {
            ThrowInvalid("RLP byte string has another length than expected");
        }

        _position = position + 1 + length;
        return buffer.Slice(position + 1, length);
    }

    /// <summary>Reads a sequence with <paramref name="func"/>, which must consume the whole payload of the sequence.</summary>
    /// <exception cref="RlpReaderException">The item is not a canonical sequence, the payload has unread items, or the nesting is too deep.</exception>
    public T ReadSequence<T>(RefRlpReaderFunc<T> func)
    {
        RlpReader reader = EnterSequence(out int itemLength);
        T result = func(ref reader);
        ExitSequence(in reader, itemLength);
        return result;
    }

    /// <inheritdoc cref="ReadSequence{T}(RefRlpReaderFunc{T})"/>
    public T ReadSequence<T, TContext>(TContext ctx, RefRlpReaderFunc<T, TContext> func)
    {
        RlpReader reader = EnterSequence(out int itemLength);
        T result = func(ref reader, ctx);
        ExitSequence(in reader, itemLength);
        return result;
    }

    /// <summary>Starts to read the next sequence without a delegate.</summary>
    /// <param name="itemLength">The length of the whole sequence item, to pass to <see cref="ExitSequence"/>.</param>
    /// <returns>A reader over the payload of the sequence. Read the payload with it, then call <see cref="ExitSequence"/>.</returns>
    /// <exception cref="RlpReaderException">The item is not a canonical sequence, or the nesting is too deep.</exception>
    public readonly RlpReader EnterSequence(out int itemLength)
    {
        // Untrusted input sets the nesting depth, so a deep input must not overflow the stack.
        int depth = _depth + 1;
        if (depth % StackCheckInterval == 0 && !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            ThrowInvalid("RLP nesting is too deep");
        }

        ReadOnlySpan<byte> buffer = _buffer;
        int position = _position;
        if ((uint)position >= (uint)buffer.Length) ThrowInvalid("RLP is truncated");

        byte prefix = buffer[position];
        int start = position + 1;
        int length = 0;
        if (prefix > RlpPrefix.LongList)
        {
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongList);
            start += prefix - RlpPrefix.LongList;
        }
        else if (prefix >= RlpPrefix.ShortList)
        {
            length = prefix - RlpPrefix.ShortList;
        }
        else
        {
            ThrowInvalid("RLP does not correspond to a sequence");
        }

        if ((long)start + length > buffer.Length) ThrowInvalid("RLP is truncated");
        itemLength = start + length - position;
        return new RlpReader(buffer.Slice(start, length), depth);
    }

    /// <summary>Ends a sequence that <see cref="EnterSequence"/> started, and moves past it.</summary>
    /// <param name="reader">The reader that <see cref="EnterSequence"/> returned.</param>
    /// <param name="itemLength">The item length that <see cref="EnterSequence"/> returned.</param>
    /// <exception cref="RlpReaderException"><paramref name="reader"/> has unread items.</exception>
    public void ExitSequence(scoped in RlpReader reader, int itemLength)
    {
        if (reader.HasNext) ThrowInvalid("RLP has trailing bytes");
        _position += itemLength;
    }

    /// <summary>Returns the result of the first alternative that reads without an <see cref="RlpReaderException"/>.</summary>
    /// <remarks>
    /// The reader goes back to its start position after each failed alternative.
    /// Nested choices can try every combination of alternatives, so keep them shallow for untrusted input.
    /// </remarks>
    /// <exception cref="RlpReaderException">No alternative matches.</exception>
    public T Choice<T>(params ReadOnlySpan<RefRlpReaderFunc<T>> alternatives)
    {
        int startingPosition = _position;
        foreach (RefRlpReaderFunc<T> f in alternatives)
        {
            try
            {
                return f(ref this);
            }
            catch (RlpReaderException)
            {
                _position = startingPosition;
            }
        }
        throw new RlpReaderException("RLP does not correspond to any alternative");
    }

    /// <summary>Reads an item with <paramref name="f"/>, or returns <see langword="null"/> when no bytes are left.</summary>
    /// <remarks>Use it for trailing optional fields. A present but malformed item still throws.</remarks>
    public T? Optional<T>(RefRlpReaderFunc<T> f, T? _ = null) where T : class
        => HasNext ? f(ref this) : null;

    /// <inheritdoc cref="Optional{T}(RefRlpReaderFunc{T}, T)"/>
    public T? Optional<T>(RefRlpReaderFunc<T> f, T? _ = null) where T : struct
        => HasNext ? f(ref this) : null;

    /// <summary>Decodes the prefix of the next item and checks that its payload fits in the buffer.</summary>
    /// <returns>The length of the prefix; zero for a single byte below <c>0x80</c>.</returns>
    private readonly int ReadHeader(out int length, out bool isList)
    {
        ReadOnlySpan<byte> buffer = _buffer;
        int position = _position;
        if ((uint)position >= (uint)buffer.Length) ThrowInvalid("RLP is truncated");

        byte prefix = buffer[position];
        int headerLength;
        if (prefix < RlpPrefix.ShortString)
        {
            length = 1;
            isList = false;
            return 0;
        }

        if (prefix <= RlpPrefix.LongString)
        {
            length = prefix - RlpPrefix.ShortString;
            isList = false;
            headerLength = 1;
            if (length == 1 && (uint)(position + 1) < (uint)buffer.Length && buffer[position + 1] < RlpPrefix.ShortString)
            {
                ThrowInvalid("RLP single byte below 0x80 must not have a prefix");
            }
        }
        else if (prefix < RlpPrefix.ShortList)
        {
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongString);
            isList = false;
            headerLength = 1 + prefix - RlpPrefix.LongString;
        }
        else if (prefix <= RlpPrefix.LongList)
        {
            length = prefix - RlpPrefix.ShortList;
            isList = true;
            headerLength = 1;
        }
        else
        {
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongList);
            isList = true;
            headerLength = 1 + prefix - RlpPrefix.LongList;
        }

        if ((long)position + headerLength + length > buffer.Length) ThrowInvalid("RLP is truncated");
        return headerLength;
    }

    // Throws from a separate method keep the hot read paths small after inlining.
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalid(string message) => throw new RlpReaderException(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadLongLength(ReadOnlySpan<byte> buffer, int position, int lengthOfLength)
    {
        // A one-byte length is the common case. A value of at most 55, including zero, must use the short form.
        if (lengthOfLength == 1 && (uint)(position + 1) < (uint)buffer.Length)
        {
            int length = buffer[position + 1];
            if (length <= RlpPrefix.MaxShortLength) ThrowInvalid("RLP length below 56 must use the short form");
            return length;
        }

        return ReadMultiByteLength(buffer, position, lengthOfLength);
    }

    private static int ReadMultiByteLength(ReadOnlySpan<byte> buffer, int position, int lengthOfLength)
    {
        if (lengthOfLength > sizeof(int)) ThrowInvalid("RLP length is too large");
        if ((long)position + 1 + lengthOfLength > buffer.Length) ThrowInvalid("RLP is truncated");

        ReadOnlySpan<byte> binaryLength = buffer.Slice(position + 1, lengthOfLength);
        if (binaryLength[0] == 0) ThrowInvalid("RLP length has leading zero bytes");

        uint length = 0;
        foreach (byte b in binaryLength)
        {
            length = (length << 8) | b;
        }

        if (length > int.MaxValue) ThrowInvalid("RLP length is too large");
        if (length <= RlpPrefix.MaxShortLength) ThrowInvalid("RLP length below 56 must use the short form");
        return (int)length;
    }
}
