// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Reads a value from an <see cref="RlpReader"/> with an extra context argument.</summary>
public delegate TResult RefRlpReaderFunc<out TResult, in TContext>(scoped ref RlpReader arg, TContext value)
    where TResult : allows ref struct;

/// <summary>Reads a value from an <see cref="RlpReader"/>.</summary>
public delegate TResult RefRlpReaderFunc<out TResult>(scoped ref RlpReader arg)
    where TResult : allows ref struct;

/// <summary>The exception that is thrown when the input is not a canonical RLP encoding of the expected shape.</summary>
public class RlpReaderException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RlpReaderException(string message) : base(message) { }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public RlpReaderException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The exception that is thrown when the input has more items or bytes than a caller allows.</summary>
public class RlpLimitException(string message) : RlpReaderException(message);

/// <summary>
/// Reads canonical RLP items from a span of bytes.
/// </summary>
/// <remarks>
/// Every malformed, truncated or non-canonical input throws <see cref="RlpReaderException"/>:
/// a single byte below <c>0x80</c> must not have a prefix, long-form prefixes must not encode lengths below 56
/// or have leading zero bytes, and integers must not have leading zero bytes.
/// A reader for a sequence only sees the payload of that sequence, so no item can extend past its parent.
/// <para>
/// The reads that have a <c>TCanonicalChecks</c> type parameter skip the checks of the canonical form with
/// <see cref="OffFlag"/>: leading zero bytes, the long form of a short length, and a prefix on a single byte below
/// <c>0x80</c>. Use it only for bytes that are known to be canonical, such as bytes that this node encoded and
/// stored. Truncated and out-of-bounds input still throws <see cref="RlpReaderException"/>.
/// The reads without the type parameter do all the checks, as <see cref="OnFlag"/> does.
/// </para>
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
            int headerLength = reader.ReadHeader<OnFlag>(out int length, out _);
            reader._position += headerLength + length;
            count++;
        }

        return count;
    }

    /// <summary>Counts the items left in this reader, and throws when there are more than <paramref name="maxCount"/>.</summary>
    /// <remarks>Use it before a collection is allocated for untrusted input, so that a peer cannot make it too large.</remarks>
    /// <exception cref="RlpLimitException">There are more than <paramref name="maxCount"/> items.</exception>
    /// <exception cref="RlpReaderException">An item prefix is malformed or truncated.</exception>
    public readonly int CountItems(int maxCount) => CountItems<OnFlag>(maxCount);

    /// <inheritdoc cref="CountItems(int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    public readonly int CountItems<TCanonicalChecks>(int maxCount) where TCanonicalChecks : struct, IFlag
    {
        RlpReader reader = this;
        int count = 0;
        while (reader.HasNext)
        {
            if (count == maxCount) ThrowLimit(maxCount);
            int headerLength = reader.ReadHeader<TCanonicalChecks>(out int length, out _);
            reader._position += headerLength + length;
            count++;
        }

        return count;
    }

    /// <summary>Gets whether the next item is a sequence.</summary>
    /// <exception cref="RlpReaderException">No bytes are left.</exception>
    public readonly bool IsNextSequence
    {
        get
        {
            if (!HasNext) ThrowInvalid("RLP is truncated");
            return _buffer[_position] >= RlpPrefix.ShortList;
        }
    }

    /// <summary>Reads an empty sequence (<c>0xc0</c>) if it is the next item.</summary>
    /// <remarks>Some stored and network formats use an empty sequence to mark a missing object.</remarks>
    /// <returns><see langword="true"/> when an empty sequence was read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadEmptySequence() => TryReadByte(RlpPrefix.ShortList);

    /// <summary>Reads an empty byte string (<c>0x80</c>) if it is the next item.</summary>
    /// <remarks>Some stored and network formats use an empty byte string to mark a missing hash or address.</remarks>
    /// <returns><see langword="true"/> when an empty byte string was read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadEmptyBytes() => TryReadByte(RlpPrefix.ShortString);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadByte(byte value)
    {
        if (_position >= _buffer.Length || _buffer[_position] != value) return false;
        _position++;
        return true;
    }

    /// <summary>Reads the next item without decoding its payload.</summary>
    /// <remarks>
    /// Only the prefix of the item is checked. The items of a sequence are not checked, so read the result with
    /// another reader before it is trusted.
    /// </remarks>
    /// <returns>The whole encoded item, with its prefix.</returns>
    /// <exception cref="RlpReaderException">The prefix is malformed or the item is truncated.</exception>
    public ReadOnlySpan<byte> ReadEncodedItem() => ReadEncodedItem<OnFlag>();

    /// <inheritdoc cref="ReadEncodedItem()"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    public ReadOnlySpan<byte> ReadEncodedItem<TCanonicalChecks>() where TCanonicalChecks : struct, IFlag
    {
        int start = _position;
        int headerLength = ReadHeader<TCanonicalChecks>(out int length, out _);
        _position = start + headerLength + length;
        return _buffer.Slice(start, headerLength + length);
    }

    /// <summary>Moves past the next item without decoding its payload.</summary>
    /// <inheritdoc cref="ReadEncodedItem" path="/exception"/>
    public void SkipItem() => ReadEncodedItem<OnFlag>();

    /// <inheritdoc cref="SkipItem()"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    public void SkipItem<TCanonicalChecks>() where TCanonicalChecks : struct, IFlag => ReadEncodedItem<TCanonicalChecks>();

    /// <summary>Reads the next <paramref name="count"/> bytes as they are, without RLP checks.</summary>
    /// <remarks>Only for formats that are not canonical RLP, such as old stored data. Canonical items use the other reads.</remarks>
    /// <exception cref="RlpReaderException">Fewer than <paramref name="count"/> bytes are left.</exception>
    public ReadOnlySpan<byte> ReadRaw(int count)
    {
        if ((uint)count > (uint)Remaining) ThrowInvalid("RLP is truncated");
        ReadOnlySpan<byte> raw = _buffer.Slice(_position, count);
        _position += count;
        return raw;
    }

    /// <summary>Reads a non-negative integer.</summary>
    /// <exception cref="RlpReaderException">The item is not a canonical integer or does not fit in <typeparamref name="T"/>.</exception>
    public T ReadInteger<T>() where T : unmanaged, IBinaryInteger<T> => ReadInteger<T, OnFlag>();

    /// <inheritdoc cref="ReadInteger{T}()"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    public unsafe T ReadInteger<T, TCanonicalChecks>() where T : unmanaged, IBinaryInteger<T> where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> bigEndian = ReadIntegerBytes<TCanonicalChecks>();
        if (bigEndian.Length > sizeof(T)) ThrowInvalid("RLP integer is too large");

        if (sizeof(T) <= sizeof(ulong))
        {
            ulong value = 0;
            foreach (byte b in bigEndian)
            {
                value = (value << 8) | b;
            }

            // A full-width value of a signed type would come out negative.
            int valueBits = sizeof(T) * 8 - (T.IsNegative(T.AllBitsSet) ? 1 : 0);
            if (value >> (valueBits - 1) > 1) ThrowInvalid("RLP integer is too large");
            return T.CreateTruncating(value);
        }

        Span<byte> buffer = stackalloc byte[sizeof(T)];
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadIntegerBytes() => ReadIntegerBytes<OnFlag>();

    /// <inheritdoc cref="ReadIntegerBytes()"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadIntegerBytes<TCanonicalChecks>() where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> bigEndian = ReadBytes<TCanonicalChecks>();
        if (TCanonicalChecks.IsActive && bigEndian.Length > 0 && bigEndian[0] == 0) ThrowInvalid("RLP integer has leading zero bytes");
        return bigEndian;
    }

    /// <summary>Reads a byte string.</summary>
    /// <returns>The payload of the byte string, without its prefix.</returns>
    /// <exception cref="RlpReaderException">The item is not a canonical byte string.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes() => ReadBytes<OnFlag>();

    /// <inheritdoc cref="ReadBytes()"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes<TCanonicalChecks>() where TCanonicalChecks : struct, IFlag
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

        if (prefix > RlpPrefix.LongString)
        {
            int longStart = ReadLongBytesStart(ref MemoryMarshal.GetReference(buffer), buffer.Length, position, prefix, TCanonicalChecks.IsActive, out int longLength);
            _position = longStart + longLength;
            return buffer.Slice(longStart, longLength);
        }

        int start = position + 1;
        int length = prefix - RlpPrefix.ShortString;
        if ((uint)(start + length) > (uint)buffer.Length) ThrowInvalid("RLP is truncated");
        if (TCanonicalChecks.IsActive && length == 1 && buffer[start] < RlpPrefix.ShortString) ThrowInvalid("RLP single byte below 0x80 must not have a prefix");

        _position = start + length;
        return buffer.Slice(start, length);
    }

    // Long strings are rare in hot paths, so their prefix decode stays out of line and the short path inlines small.
    // The buffer goes in as a reference and a length, and the result comes back as integers: a span argument or
    // result is a stack copy with a GC reference, which every caller that inlines the read must zero in its prolog.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLongBytesStart(ref byte data, int dataLength, int position, byte prefix, bool canonical, out int length)
    {
        if (prefix >= RlpPrefix.ShortList) ThrowInvalid("RLP does not correspond to a byte string");

        ReadOnlySpan<byte> buffer = MemoryMarshal.CreateReadOnlySpan(ref data, dataLength);
        length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongString, canonical);
        int start = position + 1 + prefix - RlpPrefix.LongString;
        if ((long)start + length > buffer.Length) ThrowInvalid("RLP is truncated");
        return start;
    }

    /// <summary>Reads a byte string of exactly <paramref name="length"/> bytes.</summary>
    /// <exception cref="RlpReaderException">The item is not a canonical byte string of that length.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes(int length) => ReadBytes<OnFlag>(length);

    /// <inheritdoc cref="ReadBytes(int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes<TCanonicalChecks>(int length) where TCanonicalChecks : struct, IFlag
    {
        // A string of 2 to 55 bytes has exactly one canonical prefix, so one byte compare checks prefix and length.
        if (length is < 2 or > RlpPrefix.MaxShortLength)
        {
            ReadOnlySpan<byte> bytes = ReadBytes<TCanonicalChecks>();
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly RlpReader EnterSequence(out int itemLength) => EnterSequence<OnFlag>(out itemLength);

    /// <inheritdoc cref="EnterSequence(out int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly RlpReader EnterSequence<TCanonicalChecks>(out int itemLength) where TCanonicalChecks : struct, IFlag
    {
        // Untrusted input sets the nesting depth, so a deep input must not overflow the stack.
        int depth = _depth + 1;
        if (depth % StackCheckInterval == 0) CheckStack();

        ReadOnlySpan<byte> buffer = _buffer;
        int position = _position;
        if ((uint)position >= (uint)buffer.Length) ThrowInvalid("RLP is truncated");

        byte prefix = buffer[position];
        if (prefix < RlpPrefix.ShortList) ThrowInvalid("RLP does not correspond to a sequence");

        int start = position + 1;
        int length;
        if (prefix == RlpPrefix.LongList + 1 && (uint)start < (uint)buffer.Length)
        {
            // A one-byte length (56 to 255 bytes) is common, for example a log or a receipt, so it is read inline.
            length = buffer[start];
            if (TCanonicalChecks.IsActive && length <= RlpPrefix.MaxShortLength) ThrowInvalid("RLP length below 56 must use the short form");
            start++;
            if ((uint)(start + length) > (uint)buffer.Length) ThrowInvalid("RLP is truncated");
        }
        else if (prefix > RlpPrefix.LongList)
        {
            length = ReadLongListLength(ref MemoryMarshal.GetReference(buffer), buffer.Length, position, prefix, TCanonicalChecks.IsActive, out int longStart);
            start = longStart;
        }
        else
        {
            length = prefix - RlpPrefix.ShortList;
            if ((uint)(start + length) > (uint)buffer.Length) ThrowInvalid("RLP is truncated");
        }

        itemLength = start + length - position;
        return new RlpReader(buffer.Slice(start, length), depth);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) ThrowInvalid("RLP nesting is too deep");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLongListLength(ref byte data, int dataLength, int position, byte prefix, bool canonical, out int start)
    {
        ReadOnlySpan<byte> buffer = MemoryMarshal.CreateReadOnlySpan(ref data, dataLength);
        int length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongList, canonical);
        start = position + 1 + prefix - RlpPrefix.LongList;
        if ((long)start + length > buffer.Length) ThrowInvalid("RLP is truncated");
        return length;
    }

    /// <summary>Ends a sequence that <see cref="EnterSequence"/> started, and moves past it.</summary>
    /// <param name="reader">The reader that <see cref="EnterSequence"/> returned.</param>
    /// <param name="itemLength">The item length that <see cref="EnterSequence"/> returned.</param>
    /// <exception cref="RlpReaderException"><paramref name="reader"/> has unread items.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitSequence(scoped in RlpReader reader, int itemLength)
    {
        if (reader.HasNext) ThrowInvalid("RLP has trailing bytes");
        _position += itemLength;
    }

    /// <summary>Ends a sequence that <see cref="EnterSequence"/> started, and moves past it and its unread items.</summary>
    /// <remarks>
    /// For formats that allow items to be added later, such as EIP-8 messages. The prefixes of the unread items are
    /// checked, but their payloads are not decoded.
    /// </remarks>
    /// <param name="reader">The reader that <see cref="EnterSequence"/> returned.</param>
    /// <param name="itemLength">The item length that <see cref="EnterSequence"/> returned.</param>
    /// <exception cref="RlpReaderException">An unread item has a malformed prefix.</exception>
    public void ExitSequenceIgnoringRest(scoped in RlpReader reader, int itemLength)
    {
        _ = reader.CountItems();
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
    private readonly int ReadHeader<TCanonicalChecks>(out int length, out bool isList) where TCanonicalChecks : struct, IFlag
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
            if (TCanonicalChecks.IsActive && length == 1 && (uint)(position + 1) < (uint)buffer.Length && buffer[position + 1] < RlpPrefix.ShortString)
            {
                ThrowInvalid("RLP single byte below 0x80 must not have a prefix");
            }
        }
        else if (prefix < RlpPrefix.ShortList)
        {
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongString, TCanonicalChecks.IsActive);
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
            length = ReadLongLength(buffer, position, prefix - RlpPrefix.LongList, TCanonicalChecks.IsActive);
            isList = true;
            headerLength = 1 + prefix - RlpPrefix.LongList;
        }

        if ((long)position + headerLength + length > buffer.Length) ThrowInvalid("RLP is truncated");
        return headerLength;
    }

    // Throws from a separate method keep the hot read paths small after inlining.
    [DoesNotReturn]
    private static void ThrowInvalid(string message) => throw new RlpReaderException(message);

    [DoesNotReturn]
    private static void ThrowLimit(int maxCount) => throw new RlpLimitException($"RLP has more than {maxCount} items");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadLongLength(ReadOnlySpan<byte> buffer, int position, int lengthOfLength, bool canonical)
    {
        // A one-byte length is the common case. A value of at most 55, including zero, must use the short form.
        if (lengthOfLength == 1 && (uint)(position + 1) < (uint)buffer.Length)
        {
            int length = buffer[position + 1];
            if (canonical && length <= RlpPrefix.MaxShortLength) ThrowInvalid("RLP length below 56 must use the short form");
            return length;
        }

        return ReadMultiByteLength(buffer, position, lengthOfLength, canonical);
    }

    private static int ReadMultiByteLength(ReadOnlySpan<byte> buffer, int position, int lengthOfLength, bool canonical)
    {
        if (lengthOfLength > sizeof(int)) ThrowInvalid("RLP length is too large");
        if ((long)position + 1 + lengthOfLength > buffer.Length) ThrowInvalid("RLP is truncated");

        ReadOnlySpan<byte> binaryLength = buffer.Slice(position + 1, lengthOfLength);
        if (canonical && binaryLength[0] == 0) ThrowInvalid("RLP length has leading zero bytes");

        uint length = 0;
        foreach (byte b in binaryLength)
        {
            length = (length << 8) | b;
        }

        if (length > int.MaxValue) ThrowInvalid("RLP length is too large");
        if (canonical && length <= RlpPrefix.MaxShortLength) ThrowInvalid("RLP length below 56 must use the short form");
        return (int)length;
    }
}
