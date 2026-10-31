// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core.Collections;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>Reads Ethereum values from a <see cref="RlpReader"/>.</summary>
/// <remarks>
/// Every read is canonical, as for <see cref="RlpReader"/>, and malformed input throws <see cref="RlpReaderException"/>.
/// A read that is named <c>...OrNull</c> also accepts the empty byte string (<c>0x80</c>) and returns <see langword="null"/>.
/// The reads that have a <c>TCanonicalChecks</c> type parameter skip the checks of the canonical form with
/// <see cref="OffFlag"/>, as the reads of <see cref="RlpReader"/> do; see <see cref="RlpBehaviors.TrustedInput"/>.
/// </remarks>
public static class RlpReaderExtensions
{
    private const int UInt256Bytes = 32;

    /// <summary>Gets the first byte of the next item without reading it.</summary>
    /// <exception cref="RlpReaderException">No bytes are left.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte PeekByte(this scoped in RlpReader reader)
    {
        RlpReader copy = reader;
        return copy.ReadRaw(1)[0];
    }

    /// <summary>Gets whether the next item is an empty sequence (<c>0xc0</c>), which marks a missing object.</summary>
    /// <exception cref="RlpReaderException">No bytes are left.</exception>
    public static bool IsNextItemEmptyList(this scoped in RlpReader reader) => reader.PeekByte() == Rlp.EmptyListByte;

    /// <summary>Gets whether the next item is an empty byte string (<c>0x80</c>).</summary>
    /// <exception cref="RlpReaderException">No bytes are left.</exception>
    public static bool IsNextItemEmptyByteArray(this scoped in RlpReader reader) => reader.PeekByte() == Rlp.EmptyByteArrayByte;

    /// <summary>Gets the next encoded item, with its prefix, without reading it.</summary>
    /// <exception cref="RlpReaderException">The prefix is malformed or the item is truncated.</exception>
    public static ReadOnlySpan<byte> PeekNextItem(this scoped in RlpReader reader)
    {
        RlpReader copy = reader;
        return copy.ReadEncodedItem();
    }

    /// <summary>Checks that no bytes are left.</summary>
    /// <exception cref="RlpReaderException">Bytes are left.</exception>
    public static void CheckEnd(this scoped in RlpReader reader)
    {
        if (reader.HasNext) ThrowInvalid("RLP has trailing bytes");
    }

    /// <summary>Ends a sequence that <see cref="RlpReader.EnterSequence"/> started, and moves past it.</summary>
    /// <remarks>
    /// With <see cref="RlpBehaviors.AllowExtraBytes"/>, the unread items of the sequence are skipped as
    /// <see cref="RlpReader.ExitSequenceIgnoringRest"/> does; without it, they throw as <see cref="RlpReader.ExitSequence"/> does.
    /// </remarks>
    /// <param name="reader">The reader that holds the sequence.</param>
    /// <param name="items">The reader that <see cref="RlpReader.EnterSequence"/> returned.</param>
    /// <param name="itemLength">The item length that <see cref="RlpReader.EnterSequence"/> returned.</param>
    /// <param name="rlpBehaviors">The behaviors of the decode.</param>
    /// <exception cref="RlpReaderException">Unread items are left without <see cref="RlpBehaviors.AllowExtraBytes"/>, or an unread item has a malformed prefix.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitSequence(this scoped ref RlpReader reader, scoped in RlpReader items, int itemLength, RlpBehaviors rlpBehaviors)
    {
        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != 0)
        {
            ExitSequenceIgnoringRest(ref reader, items, itemLength);
        }
        else
        {
            reader.ExitSequence(in items, itemLength);
        }
    }

    // Takes a copy of the items: a reference to them, passed to a call that is not inlined, would keep the reader
    // of the caller in memory instead of in registers for the whole decode.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExitSequenceIgnoringRest(ref RlpReader reader, RlpReader items, int itemLength)
        => reader.ExitSequenceIgnoringRest(in items, itemLength);

    /// <summary>Moves past the next <paramref name="count"/> items without decoding them.</summary>
    /// <exception cref="RlpReaderException">An item prefix is malformed or truncated.</exception>
    public static void SkipItems(this scoped ref RlpReader reader, int count)
    {
        for (int i = 0; i < count; i++)
        {
            reader.SkipItem();
        }
    }

    /// <summary>Counts the items left in <paramref name="reader"/> and checks the count against <paramref name="limit"/>.</summary>
    /// <param name="limit">The maximum count; <see langword="null"/> means <see cref="RlpLimit.DefaultLimit"/>.</param>
    /// <exception cref="RlpLimitException">There are more items than <paramref name="limit"/> allows.</exception>
    /// <exception cref="RlpReaderException">An item prefix is malformed or truncated.</exception>
    public static int CountItems(this scoped in RlpReader reader, RlpLimit? limit) => reader.CountItems<OnFlag>(limit);

    /// <inheritdoc cref="CountItems(in RlpReader, RlpLimit?)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    public static int CountItems<TCanonicalChecks>(this scoped in RlpReader reader, RlpLimit? limit) where TCanonicalChecks : struct, IFlag
    {
        RlpLimit l = limit ?? RlpLimit.DefaultLimit;
        return reader.CountItems<TCanonicalChecks>(l.Limit, l);
    }

    /// <summary>Counts the items left in <paramref name="reader"/>, and throws with the message of <paramref name="limit"/> when there are more than <paramref name="maxCount"/>.</summary>
    /// <remarks>For a bound that is tighter than <paramref name="limit"/>, for example one that the remaining bytes set.</remarks>
    internal static int CountItems(this scoped in RlpReader reader, int maxCount, RlpLimit limit)
        => reader.CountItems<OnFlag>(maxCount, limit);

    /// <inheritdoc cref="CountItems(in RlpReader, int, RlpLimit)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    internal static int CountItems<TCanonicalChecks>(this scoped in RlpReader reader, int maxCount, RlpLimit limit) where TCanonicalChecks : struct, IFlag
    {
        try
        {
            return reader.CountItems<TCanonicalChecks>(maxCount);
        }
        catch (RlpLimitException)
        {
            Rlp.ThrowCountOverLimit((uint)maxCount + 1, reader.Remaining, limit);
            throw;
        }
    }

    /// <summary>Reads a byte string and checks its length.</summary>
    /// <param name="limit">The maximum length of a byte string of two or more bytes; <see langword="null"/> means <see cref="RlpLimit.DefaultLimit"/>.</param>
    /// <param name="size">The exact length, or -1 for any length.</param>
    /// <exception cref="RlpLimitException">The byte string is longer than <paramref name="limit"/>.</exception>
    /// <exception cref="RlpReaderException">The item is not a canonical byte string, or does not have <paramref name="size"/> bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> DecodeByteArraySpan(this scoped ref RlpReader reader, RlpLimit? limit = null, int size = -1)
        => reader.DecodeByteArraySpan<OnFlag>(limit, size);

    /// <inheritdoc cref="DecodeByteArraySpan(ref RlpReader, RlpLimit?, int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> DecodeByteArraySpan<TCanonicalChecks>(this scoped ref RlpReader reader, RlpLimit? limit = null, int size = -1) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> value = reader.ReadBytes<TCanonicalChecks>();
        if (value.Length > 1) Rlp.GuardLimit(value.Length, reader.Remaining + value.Length, limit);
        Rlp.GuardSize(value.Length, size);
        return value;
    }

    /// <summary>Reads a byte string to a new array.</summary>
    /// <remarks>An empty or single-byte value can return a shared array, so do not change the result.</remarks>
    /// <inheritdoc cref="DecodeByteArraySpan" path="/param|/exception"/>
    public static byte[] DecodeByteArray(this scoped ref RlpReader reader, RlpLimit? limit = null, int size = -1)
        => reader.DecodeByteArray<OnFlag>(limit, size);

    /// <inheritdoc cref="DecodeByteArray(ref RlpReader, RlpLimit?, int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    public static byte[] DecodeByteArray<TCanonicalChecks>(this scoped ref RlpReader reader, RlpLimit? limit = null, int size = -1) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> span = reader.DecodeByteArraySpan<TCanonicalChecks>(limit, size);
        if (span.Length == 0) return [];
        if (span.Length == 1 && span[0] < Rlp.EmptyByteArrayByte) return SingleByteArrays[span[0]];
        return span.ToArray();
    }

    /// <summary>Reads a byte string as a slice of <paramref name="source"/>, which must be the buffer of <paramref name="reader"/>.</summary>
    /// <remarks>When the value is not in <paramref name="source"/>, for example a shared single byte, it returns a copy.</remarks>
    /// <inheritdoc cref="DecodeByteArraySpan" path="/param|/exception"/>
    public static Memory<byte> DecodeByteArrayMemory(this scoped ref RlpReader reader, Memory<byte> source, RlpLimit? limit = null, int size = -1)
        => reader.DecodeByteArrayMemory<OnFlag>(source, limit, size);

    /// <inheritdoc cref="DecodeByteArrayMemory(ref RlpReader, Memory{byte}, RlpLimit?, int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    public static Memory<byte> DecodeByteArrayMemory<TCanonicalChecks>(this scoped ref RlpReader reader, Memory<byte> source, RlpLimit? limit = null, int size = -1) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> span = reader.DecodeByteArraySpan<TCanonicalChecks>(limit, size);
        if (span.IsEmpty) return Memory<byte>.Empty;
        return TryGetOffset(source.Span, span, out int offset) ? source.Slice(offset, span.Length) : span.ToArray();
    }

    /// <summary>Gets the part of <paramref name="source"/> that <paramref name="part"/> refers to.</summary>
    /// <remarks>Use it to keep an item that a <see cref="RlpReader"/> over <paramref name="source"/> returned, without a copy.</remarks>
    /// <exception cref="ArgumentException"><paramref name="part"/> is not in <paramref name="source"/>.</exception>
    public static Memory<byte> SliceOf(this Memory<byte> source, ReadOnlySpan<byte> part)
    {
        if (!TryGetOffset(source.Span, part, out int offset)) throw new ArgumentException("The span is not in the memory", nameof(part));
        return source.Slice(offset, part.Length);
    }

    private static bool TryGetOffset(ReadOnlySpan<byte> source, ReadOnlySpan<byte> part, out int offset)
    {
        nint byteOffset = Unsafe.ByteOffset(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(part));
        offset = (int)byteOffset;
        return byteOffset >= 0 && byteOffset <= source.Length - part.Length;
    }

    /// <summary>Reads a sequence of byte strings.</summary>
    /// <param name="limit">The maximum count of items; <see langword="null"/> means <see cref="RlpLimit.DefaultLimit"/>.</param>
    /// <param name="innerSize">The exact length of each item, or -1 for any length.</param>
    public static byte[][] DecodeByteArrays(this scoped ref RlpReader reader, RlpLimit? limit = null, int innerSize = -1)
        => reader.DecodeByteArrays<OnFlag>(limit, innerSize);

    /// <inheritdoc cref="DecodeByteArrays(ref RlpReader, RlpLimit?, int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    public static byte[][] DecodeByteArrays<TCanonicalChecks>(this scoped ref RlpReader reader, RlpLimit? limit = null, int innerSize = -1) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        if (!items.HasNext)
        {
            reader.ExitSequence(in items, itemLength);
            return [];
        }

        // A plain new array keeps its exact type, so the stores below need no covariance check.
        byte[][] result = new byte[items.CountItems<TCanonicalChecks>(limit)][];
        for (int i = 0; i < result.Length; i++)
        {
            // A fixed size above one byte needs neither the length limit nor the shared single-byte arrays.
            result[i] = innerSize > 1
                ? items.ReadBytes<TCanonicalChecks>(innerSize).ToArray()
                : items.DecodeByteArray<TCanonicalChecks>(size: innerSize);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    /// <summary>Reads a byte string as UTF-8 text.</summary>
    public static string DecodeString(this scoped ref RlpReader reader, RlpLimit? limit = null)
        => Encoding.UTF8.GetString(reader.DecodeByteArraySpan(limit));

    /// <summary>Reads a boolean, which is <c>0x01</c> or <c>0x80</c>.</summary>
    public static bool DecodeBool(this scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> value = reader.ReadBytes();
        if (value.IsEmpty) return false;
        if (value.Length != 1 || value[0] != 1) ThrowInvalid("Unexpected value for a boolean");
        return true;
    }

    /// <summary>Reads a non-negative integer of at most 8 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DecodeULong(this scoped ref RlpReader reader) => reader.DecodeULong<OnFlag>();

    /// <inheritdoc cref="DecodeULong(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong DecodeULong<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => reader.ReadWord<TCanonicalChecks>(sizeof(ulong));

    /// <summary>Reads an integer of at most 8 bytes as a <see cref="long"/>.</summary>
    /// <remarks>A value above <see cref="long.MaxValue"/> comes out negative, as with the bytes of <c>RlpWriterExtensions.Encode(long)</c>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DecodeLong(this scoped ref RlpReader reader) => reader.DecodeLong<OnFlag>();

    /// <inheritdoc cref="DecodeLong(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DecodeLong<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => (long)reader.ReadWord<TCanonicalChecks>(sizeof(ulong));

    /// <summary>Reads a non-negative integer of at most 4 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DecodeUInt(this scoped ref RlpReader reader) => reader.DecodeUInt<OnFlag>();

    /// <inheritdoc cref="DecodeUInt(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DecodeUInt<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => (uint)reader.ReadWord<TCanonicalChecks>(sizeof(uint));

    /// <summary>Reads an integer of at most 4 bytes as an <see cref="int"/>.</summary>
    /// <remarks>A value above <see cref="int.MaxValue"/> comes out negative.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeInt(this scoped ref RlpReader reader) => reader.DecodeInt<OnFlag>();

    /// <inheritdoc cref="DecodeInt(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeInt<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => (int)reader.ReadWord<TCanonicalChecks>(sizeof(uint));

    /// <summary>Reads an integer of at most <see cref="int.MaxValue"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodePositiveInt(this scoped ref RlpReader reader) => reader.DecodePositiveInt<OnFlag>();

    /// <inheritdoc cref="DecodePositiveInt(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodePositiveInt<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        ulong value = reader.ReadWord<TCanonicalChecks>(sizeof(int));
        if (value > int.MaxValue) ThrowInvalid("RLP integer is too large");
        return (int)value;
    }

    /// <summary>Reads an integer of at most <see cref="long.MaxValue"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DecodePositiveLong(this scoped ref RlpReader reader) => reader.DecodePositiveLong<OnFlag>();

    /// <inheritdoc cref="DecodePositiveLong(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DecodePositiveLong<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        ulong value = reader.ReadWord<TCanonicalChecks>(sizeof(long));
        if (value > long.MaxValue) ThrowInvalid("RLP integer is too large");
        return (long)value;
    }

    /// <summary>Reads a non-negative integer of at most 2 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort DecodeUShort(this scoped ref RlpReader reader) => reader.DecodeUShort<OnFlag>();

    /// <inheritdoc cref="DecodeUShort(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort DecodeUShort<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => (ushort)reader.ReadWord<TCanonicalChecks>(sizeof(ushort));

    /// <summary>Reads a non-negative integer of at most 1 byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte DecodeByte(this scoped ref RlpReader reader) => reader.DecodeByte<OnFlag>();

    /// <inheritdoc cref="DecodeByte(ref RlpReader)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte DecodeByte<TCanonicalChecks>(this scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag => (byte)reader.ReadWord<TCanonicalChecks>(sizeof(byte));

    /// <summary>Reads a canonical integer of at most <paramref name="maxLength"/> bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadWord<TCanonicalChecks>(this scoped ref RlpReader reader, int maxLength) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> bytes = reader.ReadIntegerBytes<TCanonicalChecks>();
        if (bytes.Length > maxLength) ThrowInvalid("RLP integer is too large");
        return Word(bytes, 0, bytes.Length);
    }

    /// <summary>Reads a non-negative integer of at most 32 bytes.</summary>
    /// <param name="length">
    /// The exact length of the byte string, or -1 for a canonical integer. With a length, leading zero bytes are
    /// allowed, as for EIP-2930 storage keys.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt256 DecodeUInt256(this scoped ref RlpReader reader, int length = -1) => reader.DecodeUInt256<OnFlag>(length);

    /// <inheritdoc cref="DecodeUInt256(ref RlpReader, int)"/>
    /// <typeparam name="TCanonicalChecks"><see cref="OnFlag"/> to check the canonical form; <see cref="OffFlag"/> to skip it, for trusted input only.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt256 DecodeUInt256<TCanonicalChecks>(this scoped ref RlpReader reader, int length = -1) where TCanonicalChecks : struct, IFlag
    {
        ReadOnlySpan<byte> bytes;
        if (length == -1)
        {
            bytes = reader.ReadIntegerBytes<TCanonicalChecks>();
            if (bytes.Length > UInt256Bytes) ThrowInvalid("RLP integer is too large");
        }
        else
        {
            if ((uint)length > UInt256Bytes) ThrowInvalid("RLP integer is too large");
            bytes = reader.ReadBytes<TCanonicalChecks>(length);
            if (TCanonicalChecks.IsActive && length == 1 && bytes[0] == 0) ThrowInvalid("RLP integer has leading zero bytes");
        }

        return ToUInt256(bytes);
    }

    /// <summary>Reads a non-negative integer of at most 32 bytes into a right-aligned word.</summary>
    public static EvmWord DecodeEvmWord(this scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadIntegerBytes();
        if (bytes.Length > UInt256Bytes) ThrowInvalid("RLP integer is too large");

        EvmWord value = default;
        Span<byte> destination = MemoryMarshal.CreateSpan(ref Unsafe.As<EvmWord, byte>(ref value), UInt256Bytes);
        bytes.CopyTo(destination[(UInt256Bytes - bytes.Length)..]);
        return value;
    }

    /// <summary>Reads a non-negative integer of at most 32 bytes.</summary>
    public static BigInteger DecodeUBigInt(this scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.DecodeByteArraySpan(RlpLimit.L32);
        if (bytes.Length >= 1 && bytes[0] == 0) ThrowInvalid("RLP integer has leading zero bytes");
        return bytes.ToUnsignedBigInteger();
    }

    /// <summary>Reads a 32-byte hash.</summary>
    /// <remarks>Returns the shared instances for <see cref="Keccak.OfAnEmptyString"/> and <see cref="Keccak.EmptyTreeHash"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Hash256 DecodeKeccak(this scoped ref RlpReader reader) => InternKeccak(reader.ReadBytes(Hash256.Size));

    /// <inheritdoc cref="DecodeKeccak"/>
    public static Hash256? DecodeKeccakOrNull(this scoped ref RlpReader reader)
        => reader.TryReadEmptyBytes() ? null : reader.DecodeKeccak();

    /// <summary>Reads a 32-byte hash, or <see langword="null"/> for <c>0x80</c>.</summary>
    public static ValueHash256? DecodeValueKeccak(this scoped ref RlpReader reader)
        => reader.TryReadEmptyBytes() ? null : new ValueHash256(reader.ReadBytes(Hash256.Size));

    /// <summary>Reads a 32-byte hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueHash256 DecodeValueKeccakNonNull(this scoped ref RlpReader reader) => new(reader.ReadBytes(Hash256.Size));

    /// <summary>Reads a 32-byte hash, or <c>0x80</c>.</summary>
    /// <returns><see langword="false"/> for <c>0x80</c>.</returns>
    public static bool TryDecodeValueKeccak(this scoped ref RlpReader reader, out ValueHash256 keccak)
    {
        if (reader.TryReadEmptyBytes())
        {
            keccak = default;
            return false;
        }

        keccak = new ValueHash256(reader.ReadBytes(Hash256.Size));
        return true;
    }

    /// <summary>Reads a hash that is stored without its leading zero bytes, or <see langword="null"/> for <c>0x80</c>.</summary>
    public static Hash256? DecodeZeroPrefixKeccak(this scoped ref RlpReader reader)
    {
        if (reader.TryReadEmptyBytes()) return null;

        Span<byte> bytes = stackalloc byte[Hash256.Size];
        reader.DecodeZeroPrefixKeccakBytes(bytes);
        return new Hash256(bytes);
    }

    /// <summary>Reads a hash that is stored without its leading zero bytes.</summary>
    /// <exception cref="RlpReaderException">The item is <c>0x80</c>.</exception>
    public static Hash256 DecodeZeroPrefixKeccakNonNull(this scoped ref RlpReader reader)
        => reader.DecodeZeroPrefixKeccak() ?? ThrowNullDecodedValue<Hash256>();

    /// <summary>Reads the topic 0 of a log, stored without its leading zero bytes, and shares the instance with <see cref="LogTopicCache"/>.</summary>
    /// <exception cref="RlpReaderException">The item is <c>0x80</c>.</exception>
    public static Hash256 DecodeZeroPrefixLogTopic0(this scoped ref RlpReader reader)
    {
        if (reader.TryReadEmptyBytes()) ThrowNullDecodedValue<Hash256>();

        Span<byte> bytes = stackalloc byte[Hash256.Size];
        reader.DecodeZeroPrefixKeccakBytes(bytes);
        return LogTopicCache.Get(bytes);
    }

    /// <summary>Reads the topic 0 of a log and shares the instance with <see cref="LogTopicCache"/>.</summary>
    public static Hash256 DecodeLogTopic0(this scoped ref RlpReader reader) => LogTopicCache.Get(reader.ReadBytes(Hash256.Size));

    private static void DecodeZeroPrefixKeccakBytes(this scoped ref RlpReader reader, scoped Span<byte> bytes)
    {
        ReadOnlySpan<byte> span = reader.DecodeByteArraySpan(RlpLimit.L32);
        bytes.Clear();
        span.CopyTo(bytes[(Hash256.Size - span.Length)..]);
    }

    /// <summary>Reads a 32-byte hash, or <c>0x80</c> as <see cref="Keccak.Zero"/>, without a copy.</summary>
    public static void DecodeKeccakStructRef(this scoped ref RlpReader reader, out Hash256StructRef keccak)
        => keccak = reader.TryReadEmptyBytes()
            ? new Hash256StructRef(Keccak.Zero.Bytes)
            : new Hash256StructRef(InternKeccakBytes(reader.ReadBytes(Hash256.Size)));

    /// <summary>Reads a hash that can be stored without its leading zero bytes, or <c>0x80</c> as <see cref="Keccak.Zero"/>.</summary>
    /// <param name="buffer">A 32-byte buffer for a hash that has fewer bytes; the result can refer to it.</param>
    public static void DecodeZeroPrefixedKeccakStructRef(this scoped ref RlpReader reader, out Hash256StructRef keccak, Span<byte> buffer)
    {
        if (reader.TryReadEmptyBytes())
        {
            keccak = new Hash256StructRef(Keccak.Zero.Bytes);
            return;
        }

        ReadOnlySpan<byte> span = reader.DecodeByteArraySpan(RlpLimit.L32);
        if (span.Length == Hash256.Size)
        {
            keccak = new Hash256StructRef(InternKeccakBytes(span));
            return;
        }

        buffer[..(Hash256.Size - span.Length)].Clear();
        span.CopyTo(buffer[(Hash256.Size - span.Length)..]);
        keccak = new Hash256StructRef(buffer);
    }

    /// <summary>Reads a 20-byte address.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Address DecodeAddress(this scoped ref RlpReader reader) => new(reader.ReadBytes(Address.Size));

    /// <summary>Reads a 20-byte address, or <see langword="null"/> for <c>0x80</c>.</summary>
    public static Address? DecodeAddressOrNull(this scoped ref RlpReader reader)
        => reader.TryReadEmptyBytes() ? null : new Address(reader.ReadBytes(Address.Size));

    /// <summary>Reads a 20-byte address, or <c>0x80</c> as <see cref="Address.Zero"/>, without a copy.</summary>
    public static void DecodeAddressStructRef(this scoped ref RlpReader reader, out AddressStructRef address)
        => address = new AddressStructRef(reader.TryReadEmptyBytes() ? Address.Zero.Bytes : reader.ReadBytes(Address.Size));

    /// <summary>Reads a 20-byte address without a copy.</summary>
    /// <exception cref="RlpReaderException">The item is <c>0x80</c>.</exception>
    public static void DecodeAddressStructRefNonNull(this scoped ref RlpReader reader, out AddressStructRef address)
    {
        if (reader.TryReadEmptyBytes()) ThrowNullDecodedValue<Address>();
        address = new AddressStructRef(reader.ReadBytes(Address.Size));
    }

    /// <summary>Reads a bloom that is a 256-byte string.</summary>
    /// <remarks>Returns the shared <see cref="Bloom.Empty"/> for an empty bloom.</remarks>
    public static Bloom DecodeBloom(this scoped ref RlpReader reader) => CreateBloom(reader.ReadBytes(Bloom.ByteLength));

    /// <summary>Reads a bloom, or <see langword="null"/> for <c>0x80</c>.</summary>
    /// <remarks>
    /// Also accepts the old sequence form that some nodes send for receipt blooms,
    /// see https://github.com/NethermindEth/nethermind/issues/113.
    /// </remarks>
    public static Bloom? DecodeBloomOrNull(this scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> bloomBytes = reader.DecodeBloomSpan();
        return bloomBytes.IsEmpty ? null : CreateBloom(bloomBytes);
    }

    /// <inheritdoc cref="DecodeBloomOrNull"/>
    /// <exception cref="RlpReaderException">The item is <c>0x80</c>.</exception>
    public static Bloom DecodeBloomNonNull(this scoped ref RlpReader reader)
        => reader.DecodeBloomOrNull() ?? ThrowNullDecodedValue<Bloom>();

    /// <inheritdoc cref="DecodeBloomOrNull"/>
    /// <remarks><c>0x80</c> reads as <see cref="Bloom.ZeroLength"/> only when <paramref name="rlpBehaviors"/> has <see cref="RlpBehaviors.Eip7668Receipts"/>.</remarks>
    /// <exception cref="RlpReaderException">The item is <c>0x80</c> and EIP-7668 blooms are not allowed.</exception>
    public static Bloom DecodeBloomNonNull(this scoped ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => reader.DecodeBloomOrNull()
            ?? ((rlpBehaviors & RlpBehaviors.Eip7668Receipts) != 0 ? Bloom.ZeroLength : ThrowNullDecodedValue<Bloom>());

    /// <summary>Reads a bloom that is a 256-byte string, or <c>0x80</c> as <see cref="Bloom.ZeroLength"/>.</summary>
    /// <remarks>For decoders that check EIP-7668 activation after the header is decoded.</remarks>
    public static Bloom DecodeBloomOrZeroLength(this scoped ref RlpReader reader)
    {
        ReadOnlySpan<byte> bloomBytes = reader.DecodeByteArraySpan(RlpLimit.Bloom);
        return bloomBytes.IsEmpty ? Bloom.ZeroLength : CreateBloom(bloomBytes);
    }

    /// <summary>Reads a bloom without a copy; <c>0x80</c> reads as <see cref="Bloom.Empty"/>.</summary>
    public static void DecodeBloomStructRef(this scoped ref RlpReader reader, out BloomStructRef bloom)
        => reader.DecodeBloomStructRef(out bloom, out _);

    /// <inheritdoc cref="DecodeBloomStructRef(ref RlpReader, out BloomStructRef)"/>
    /// <param name="wasMissing"><see langword="true"/> when the item is <c>0x80</c>.</param>
    public static void DecodeBloomStructRef(this scoped ref RlpReader reader, out BloomStructRef bloom, out bool wasMissing)
    {
        ReadOnlySpan<byte> bloomBytes = reader.DecodeBloomSpan();
        wasMissing = bloomBytes.IsEmpty;
        if (wasMissing)
        {
            bloom = new BloomStructRef(Bloom.Empty.Bytes);
            return;
        }

        if (bloomBytes.Length != Bloom.ByteLength) ThrowInvalid("Incorrect bloom RLP");
        bloom = bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? new BloomStructRef(Bloom.Empty.Bytes) : new BloomStructRef(bloomBytes);
    }

    /// <summary>Reads the bytes of a bloom, also in the old sequence form; an empty result is <c>0x80</c>.</summary>
    private static ReadOnlySpan<byte> DecodeBloomSpan(this scoped ref RlpReader reader)
    {
        // The old form is 249 1 2 129 127, then the 256 bytes of the bloom.
        const byte OldFormPrefix = 249;
        const int OldFormHeaderLength = 5;
        if (reader.PeekByte() == OldFormPrefix)
        {
            reader.ReadRaw(OldFormHeaderLength);
            return reader.ReadRaw(Bloom.ByteLength);
        }

        return reader.DecodeByteArraySpan(RlpLimit.Bloom);
    }

    private static Bloom CreateBloom(ReadOnlySpan<byte> bloomBytes)
    {
        if (bloomBytes.Length != Bloom.ByteLength) ThrowInvalid("Incorrect bloom RLP");
        return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes);
    }

    /// <summary>Reads a sequence of non-null items with <paramref name="decoder"/>.</summary>
    /// <param name="decoder">The decoder; <see langword="null"/> means the registered decoder of <typeparamref name="T"/>.</param>
    /// <param name="limit">The maximum count of items; <see langword="null"/> means <see cref="RlpLimit.DefaultLimit"/>.</param>
    /// <exception cref="RlpReaderException">An item is <c>0xc0</c> or decodes as <see langword="null"/>.</exception>
    public static T[] DecodeNonNullArray<T>(this scoped ref RlpReader reader, IRlpDecoder<T>? decoder = null, RlpLimit? limit = null)
        => (decoder ?? Rlp.GetDecoderOrThrow<T>()).DecodeNonNullArray(ref reader, RlpBehaviors.None, limit);

    /// <summary>Reads a sequence of items with <paramref name="decoder"/>, and reads <c>0xc0</c> as <paramref name="defaultElement"/>.</summary>
    /// <inheritdoc cref="DecodeNonNullArray{T}" path="/param"/>
    public static T?[] DecodeNullableArray<T>(this scoped ref RlpReader reader, IRlpDecoder<T>? decoder = null, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
    {
        decoder ??= Rlp.GetDecoderOrThrow<T>();
        RlpReader items = reader.EnterSequence(out int itemLength);
        T?[] result = new T?[items.CountItems(limit)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = items.TryReadEmptySequence() ? defaultElement : decoder.Decode(ref items);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    /// <summary>Reads a sequence with <paramref name="decoder"/>.</summary>
    /// <param name="allowNulls">When <see langword="true"/>, <c>0xc0</c> reads as <paramref name="defaultElement"/>; otherwise it throws.</param>
    /// <inheritdoc cref="DecodeNonNullArray{T}" path="/param"/>
    public static T?[] DecodeArray<T>(this scoped ref RlpReader reader, IRlpDecoder<T>? decoder = null, bool allowNulls = false, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
        => allowNulls ? reader.DecodeNullableArray(decoder, defaultElement, limit) : reader.DecodeNonNullArray(decoder, limit);

    /// <summary>Reads a sequence of non-null items with <paramref name="decodeItem"/>, and reads <c>0xc0</c> as <paramref name="defaultElement"/>.</summary>
    /// <param name="decodeItem">Reads one item.</param>
    /// <param name="defaultElement">The item for <c>0xc0</c>; <see langword="null"/> or a value type makes <c>0xc0</c> throw.</param>
    /// <param name="limit">The maximum count of items; <see langword="null"/> means <see cref="RlpLimit.DefaultLimit"/>.</param>
    /// <exception cref="RlpReaderException">An item is <c>0xc0</c> without a default element, or decodes as <see langword="null"/>.</exception>
    public static T[] DecodeNonNullArray<T>(this scoped ref RlpReader reader, RefRlpReaderFunc<T> decodeItem, T? defaultElement = default, RlpLimit? limit = null)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        T[] result = new T[items.CountItems(limit)];
        bool hasDefaultElement = defaultElement is not null && !typeof(T).IsValueType;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = DecodeNonNullItem(ref items, decodeItem, hasDefaultElement, defaultElement, decodeEmptyList: false, i);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    /// <summary>Reads a sequence of items with <paramref name="decodeItem"/>, and reads <c>0xc0</c> as <paramref name="defaultElement"/>.</summary>
    /// <inheritdoc cref="DecodeNonNullArray{T}(ref RlpReader, RefRlpReaderFunc{T}, T, RlpLimit?)" path="/param"/>
    public static T?[] DecodeArray<T>(this scoped ref RlpReader reader, RefRlpReaderFunc<T?> decodeItem, T? defaultElement = default, RlpLimit? limit = null)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        T?[] result = new T?[items.CountItems(limit)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = items.TryReadEmptySequence() ? defaultElement : decodeItem(ref items);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    /// <inheritdoc cref="DecodeArray{T}(ref RlpReader, RefRlpReaderFunc{T}, T, RlpLimit?)"/>
    public static T?[] DecodeNullableArray<T>(this scoped ref RlpReader reader, RefRlpReaderFunc<T?> decodeItem, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
        => reader.DecodeArray(decodeItem, defaultElement, limit);

    /// <summary>Reads a sequence of non-null items with <paramref name="decodeItem"/> into a pooled list that the caller disposes.</summary>
    /// <param name="decodeEmptyList">When <see langword="true"/>, <c>0xc0</c> goes to <paramref name="decodeItem"/> instead of being a missing item.</param>
    /// <inheritdoc cref="DecodeNonNullArray{T}(ref RlpReader, RefRlpReaderFunc{T}, T, RlpLimit?)" path="/param"/>
    /// <exception cref="RlpReaderException">The sequence or an item is malformed; the items read so far are disposed.</exception>
    public static ArrayPoolList<T> DecodeNonNullArrayPoolList<T>(this scoped ref RlpReader reader, RefRlpReaderFunc<T> decodeItem, T? defaultElement = default, RlpLimit? limit = null, bool decodeEmptyList = false)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        int count = items.CountItems(limit);
        ArrayPoolList<T> result = new(count, count);
        bool hasDefaultElement = defaultElement is not null && !typeof(T).IsValueType;
        int i = 0;
        try
        {
            for (; i < count; i++)
            {
                result[i] = DecodeNonNullItem(ref items, decodeItem, hasDefaultElement, defaultElement, decodeEmptyList, i);
            }

            reader.ExitSequence(in items, itemLength);
            return result;
        }
        catch (RlpReaderException)
        {
            Rlp.DisposeDecodedItemsAndList(result, i);
            throw;
        }
        catch (Exception e)
        {
            Rlp.DisposeDecodedItemsAndList(result, i);
            throw new RlpReaderException($"Error decoding array of {typeof(T).Name}.", e);
        }
    }

    private static T DecodeNonNullItem<T>(ref RlpReader items, RefRlpReaderFunc<T> decodeItem, bool hasDefaultElement, T? defaultElement, bool decodeEmptyList, int index)
    {
        if (!decodeEmptyList && items.TryReadEmptySequence())
        {
            if (!hasDefaultElement) ThrowInvalid($"RLP array item {index} is null");
            return defaultElement!;
        }

        return decodeItem(ref items) ?? throw new RlpReaderException($"RLP array item {index} is null");
    }

    /// <summary>Builds a <see cref="UInt256"/> from the big-endian bytes of an integer of at most 32 bytes.</summary>
    /// <remarks>
    /// Most values are shorter than 32 bytes. The <see cref="UInt256"/> constructor runs eight iterations per word for
    /// every length, so the words are built here instead, with one pass over the bytes.
    /// </remarks>
    internal static UInt256 ToUInt256(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.Length;
        if (length <= sizeof(ulong))
        {
            return new UInt256(Word(bytes, 0, length));
        }

        if (length == UInt256Bytes)
        {
            return Bytes.ReadUInt256BigEndian(bytes);
        }

        int end1 = length - sizeof(ulong);
        int end2 = end1 > sizeof(ulong) ? end1 - sizeof(ulong) : 0;
        int end3 = end2 > sizeof(ulong) ? end2 - sizeof(ulong) : 0;
        return new UInt256(Word(bytes, end1, length), Word(bytes, end2, end1), Word(bytes, end3, end2), Word(bytes, 0, end3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Word(ReadOnlySpan<byte> bytes, int start, int end)
    {
        ulong word = 0;
        for (int i = start; i < end; i++)
        {
            word = (word << 8) | bytes[i];
        }

        return word;
    }

    // Leading words of Keccak.OfAnEmptyString and Keccak.EmptyTreeHash as FirstWord reads them, so that a hash that
    // is not one of them needs no 32-byte compare.
    internal const ulong OfAnEmptyStringFirstWord = 0x3c23f7860146d2c5;
    internal const ulong EmptyTreeHashFirstWord = 0xa655cc1b171fe856;

    /// <summary>Returns the shared instance for the two hashes that are most common in accounts.</summary>
    internal static Hash256 InternKeccak(ReadOnlySpan<byte> span)
    {
        ulong first = FirstWord(span);
        if (first == OfAnEmptyStringFirstWord)
        {
            if (span.SequenceEqual(Keccak.OfAnEmptyString.Bytes)) return Keccak.OfAnEmptyString;
        }
        else if (first == EmptyTreeHashFirstWord)
        {
            if (span.SequenceEqual(Keccak.EmptyTreeHash.Bytes)) return Keccak.EmptyTreeHash;
        }

        return new Hash256(span);
    }

    private static ReadOnlySpan<byte> InternKeccakBytes(ReadOnlySpan<byte> span)
    {
        if (span.SequenceEqual(Keccak.OfAnEmptyString.Bytes)) return Keccak.OfAnEmptyString.Bytes;
        if (span.SequenceEqual(Keccak.EmptyTreeHash.Bytes)) return Keccak.EmptyTreeHash.Bytes;
        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong FirstWord(ReadOnlySpan<byte> span)
    {
        Debug.Assert(span.Length >= Hash256.Size);
        return Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(span));
    }

    private static byte[][] SingleByteArrays => field ??= CreateSingleByteArrays();

    private static byte[][] CreateSingleByteArrays()
    {
        byte[][] arrays = new byte[Rlp.EmptyByteArrayByte][];
        for (int i = 0; i < arrays.Length; i++)
        {
            arrays[i] = [(byte)i];
        }

        return arrays;
    }

    [DoesNotReturn, StackTraceHidden]
    internal static T ThrowNullDecodedValue<T>() => throw new RlpReaderException($"{typeof(T).Name} decoded as null");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalid(string message) => throw new RlpReaderException(message);
}
