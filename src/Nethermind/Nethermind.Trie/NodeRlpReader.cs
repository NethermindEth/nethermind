// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

/// <summary>Reads the items of a trie node RLP at byte offsets.</summary>
/// <remarks>
/// Node RLP is found by its hash, so it is trusted: the reads skip the canonical-form checks, and a short item is not
/// checked against the end of the data, as the slice that reads it checks that. A long prefix is checked, and a bad
/// one throws <see cref="RlpReaderException"/>. A trie node keeps the offsets of its children, so the reads take an
/// offset instead of keeping a cursor. The instance methods forward to static ones that take the data and the offset
/// by value, so the offset of the caller can stay in a register.
/// </remarks>
internal readonly ref struct NodeRlpReader(ReadOnlySpan<byte> data)
{
    /// <summary>The prefix of a 32-byte string, such as a child hash.</summary>
    public const byte KeccakRlpPrefix = 0xa0;

    private const int ShortStringOffset = 0x80;
    private const int ShortStringMaxPrefix = 0xb7;
    private const int ListOffset = 0xc0;
    private const int ShortListMaxPrefix = 0xf7;
    private const int SmallPrefixBarrier = 56;

    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>The prefix length by the prefix byte: 0 for a single byte, 1 for a short form, 2-9 for a long form.</summary>
    private static ReadOnlySpan<byte> PrefixLengthData =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9,
    ];

    /// <summary>The whole length of a short-form item by its prefix byte; zero for a long-form prefix.</summary>
    private static ReadOnlySpan<byte> TotalRlpLengthData =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        49, 50, 51, 52, 53, 54, 55, 56, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        49, 50, 51, 52, 53, 54, 55, 56, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    public ReadOnlySpan<byte> Data => _data;

    /// <summary>Gets the prefix and payload lengths of the item at <paramref name="position"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength(int position)
        => PeekPrefixAndContentLength(_data, position);

    /// <summary>Gets the length of the item at <paramref name="position"/>, with its prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength(int position) => PeekNextRlpLength(_data, position);

    /// <summary>Moves <paramref name="position"/> past the prefix of the item there, without decoding its length.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipLength(scoped ref int position) => position += GetPrefixLength(_data[position]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSequenceNext(int position) => _data[position] >= ListOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem(scoped ref int position) => position += PeekNextRlpLength(_data, position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItems(scoped ref int position, int count) => position = SkipItems(_data, position, count);

    /// <summary>Moves <paramref name="position"/> past the prefix of the sequence there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSequenceLength(scoped ref int position, out int contentLength)
        => position = ReadSequenceLength(_data, position, out contentLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value)
        => position = DecodeByteArraySpan(_data, position, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeKeccak(scoped ref int position, out Hash256 keccak)
        => position = DecodeKeccak(_data, position, out keccak);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeValueKeccakNonNull(scoped ref int position, out ValueHash256 keccak)
        => position = DecodeValueKeccakNonNull(_data, position, out keccak);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPrefixLength(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetReference(PrefixLengthData), prefixByte);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetTotalRlpLength(int prefixByte)
    {
        Debug.Assert((uint)prefixByte <= byte.MaxValue);
        return Unsafe.Add(ref MemoryMarshal.GetReference(TotalRlpLengthData), prefixByte);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int PrefixLength, int ContentLength) PeekPrefixAndContentLength(ReadOnlySpan<byte> data, int position)
    {
        int prefix = data[position];
        return prefix switch
        {
            < ShortStringOffset => (0, 1),
            <= ShortStringMaxPrefix => (1, prefix - ShortStringOffset),
            < ListOffset => PeekLongPrefixAndContentLength(data, position, prefix - ShortStringMaxPrefix),
            <= ShortListMaxPrefix => (1, prefix - ListOffset),
            _ => PeekLongPrefixAndContentLength(data, position, prefix - ShortListMaxPrefix)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PeekNextRlpLength(ReadOnlySpan<byte> data, int position)
    {
        int prefix = data[position];
        int totalLength = GetTotalRlpLength(prefix);
        if (totalLength == 0) totalLength = PeekLongRlpLength(data, position, prefix);
        return totalLength;
    }

    private static int SkipItems(ReadOnlySpan<byte> data, int position, int count)
    {
        for (int i = 0; i < count; i++)
        {
            position += PeekNextRlpLength(data, position);
        }

        return position;
    }

    private static int ReadSequenceLength(ReadOnlySpan<byte> data, int position, out int contentLength)
    {
        int prefix = data[position++];
        if (prefix < ListOffset)
        {
            ThrowInvalid("RLP does not correspond to a sequence");
        }

        if (prefix <= ShortListMaxPrefix)
        {
            contentLength = prefix - ListOffset;
            return position;
        }

        int lengthOfLength = prefix - ShortListMaxPrefix;
        if (lengthOfLength > sizeof(int))
        {
            ThrowInvalid("RLP length is too large");
        }

        contentLength = DeserializeLengthRef(ref MemoryMarshal.GetReference(data.Slice(position, lengthOfLength)), lengthOfLength);
        if (contentLength < SmallPrefixBarrier)
        {
            ThrowInvalid("RLP length below 56 must use the short form");
        }

        return position + lengthOfLength;
    }

    private static int DecodeByteArraySpan(ReadOnlySpan<byte> data, int position, out ReadOnlySpan<byte> value)
    {
        int prefix = data[position];
        if (prefix < ShortStringOffset)
        {
            value = data.Slice(position, 1);
            return position + 1;
        }

        if (prefix <= ShortStringMaxPrefix)
        {
            int length = prefix - ShortStringOffset;
            value = data.Slice(position + 1, length);
            return position + 1 + length;
        }

        if (prefix >= ListOffset)
        {
            ThrowInvalid("RLP does not correspond to a byte string");
        }

        (int prefixLength, int contentLength) = PeekLongPrefixAndContentLength(data, position, prefix - ShortStringMaxPrefix);
        value = data.Slice(position + prefixLength, contentLength);
        return position + prefixLength + contentLength;
    }

    private static int DecodeKeccak(ReadOnlySpan<byte> data, int position, out Hash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            ThrowInvalid("RLP byte string has another length than expected");
        }

        keccak = RlpReaderExtensions.InternKeccak(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    private static int DecodeValueKeccakNonNull(ReadOnlySpan<byte> data, int position, out ValueHash256 keccak)
    {
        int prefix = data[position++];
        if (prefix != KeccakRlpPrefix)
        {
            ThrowInvalid("RLP byte string has another length than expected");
        }

        keccak = new ValueHash256(data.Slice(position, Hash256.Size));
        return position + Hash256.Size;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int PrefixLength, int ContentLength) PeekLongPrefixAndContentLength(ReadOnlySpan<byte> data, int position, int lengthOfLength)
    {
        if ((uint)lengthOfLength > sizeof(int))
        {
            ThrowInvalid("RLP length is too large");
        }

        if (lengthOfLength > data.Length - position - 1)
        {
            ThrowInvalid("RLP is truncated");
        }

        int contentLength = DeserializeLengthRef(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), position + 1), lengthOfLength);
        if (contentLength < SmallPrefixBarrier)
        {
            ThrowInvalid("RLP length below 56 must use the short form");
        }

        if (contentLength > data.Length - position - (1 + lengthOfLength))
        {
            ThrowInvalid("RLP is truncated");
        }

        return (1 + lengthOfLength, contentLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PeekLongRlpLength(ReadOnlySpan<byte> data, int position, int prefix)
    {
        int lengthOfLength = prefix < ListOffset ? prefix - ShortStringMaxPrefix : prefix - ShortListMaxPrefix;
        (int prefixLength, int contentLength) = PeekLongPrefixAndContentLength(data, position, lengthOfLength);
        return prefixLength + contentLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DeserializeLengthRef(ref byte firstElement, int lengthOfLength)
    {
        int result = firstElement;
        if (result == 0)
        {
            ThrowInvalid("RLP length starts with 0");
        }

        if (lengthOfLength == 1)
        {
            // Already read above
        }
        else if (lengthOfLength == 2)
        {
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
        }
        else if (lengthOfLength == 3)
        {
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                | (result << 16);
        }
        else
        {
            result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
        }

        return result;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalid(string message) => throw new RlpReaderException(message);
}
