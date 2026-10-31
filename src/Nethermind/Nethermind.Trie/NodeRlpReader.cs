// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// Node RLP is found by its hash, so it is trusted: the reads skip the canonical-form checks. Truncated input still
/// throws <see cref="RlpReaderException"/>. A trie node keeps the offsets of its children, so the reads take an
/// offset instead of keeping a cursor.
/// </remarks>
internal readonly ref struct NodeRlpReader(ReadOnlySpan<byte> data)
{
    /// <summary>The prefix of a 32-byte string, such as a child hash.</summary>
    public const byte KeccakRlpPrefix = 0xa0;

    private const int ShortStringMaxPrefix = 0xb7;
    private const int ShortListMaxPrefix = 0xf7;

    private readonly ReadOnlySpan<byte> _data = data;

    /// <summary>The whole length of a short-form item by its prefix byte; zero for a long-form prefix.</summary>
    private static ReadOnlySpan<byte> ShortItemLengths =>
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
    {
        int prefixLength = ReadPrefix(position, out int contentLength);
        return (prefixLength, contentLength);
    }

    /// <summary>Gets the length of the item at <paramref name="position"/>, with its prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength(int position)
    {
        ReadOnlySpan<byte> data = _data;
        if ((uint)position >= (uint)data.Length) ThrowInvalid("RLP is truncated");

        int prefix = data[position];
        int length = Unsafe.Add(ref MemoryMarshal.GetReference(ShortItemLengths), prefix);
        if (length == 0) return ReadLongItemLength(data, position, prefix);

        if (position + length > data.Length) ThrowInvalid("RLP is truncated");
        return length;
    }

    /// <summary>Moves <paramref name="position"/> past the prefix of the item there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipLength(scoped ref int position) => position += ReadPrefix(position, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSequenceNext(int position) => _data[position] >= Rlp.EmptyListByte;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem(scoped ref int position) => position += PeekNextRlpLength(position);

    public void SkipItems(scoped ref int position, int count)
    {
        ReadOnlySpan<byte> data = _data;
        int end = position;
        for (int i = 0; i < count; i++)
        {
            if ((uint)end >= (uint)data.Length) ThrowInvalid("RLP is truncated");

            int prefix = data[end];
            int length = Unsafe.Add(ref MemoryMarshal.GetReference(ShortItemLengths), prefix);
            end += length != 0 ? length : ReadLongItemLength(data, end, prefix);
        }

        // Every item starts inside the data, so only the end of the last one can be past it.
        if (end > data.Length) ThrowInvalid("RLP is truncated");
        position = end;
    }

    /// <summary>Moves <paramref name="position"/> past the prefix of the sequence there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSequenceLength(scoped ref int position, out int contentLength)
    {
        if ((uint)position < (uint)_data.Length && _data[position] < Rlp.EmptyListByte) ThrowInvalid("RLP does not correspond to a sequence");
        position += ReadPrefix(position, out contentLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value)
    {
        if ((uint)position < (uint)_data.Length && _data[position] >= Rlp.EmptyListByte) ThrowInvalid("RLP does not correspond to a byte string");
        int prefixLength = ReadPrefix(position, out int contentLength);
        value = _data.Slice(position + prefixLength, contentLength);
        position += prefixLength + contentLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeKeccak(scoped ref int position, out Hash256 keccak)
    {
        RlpReader reader = new(_data[position..]);
        keccak = reader.DecodeKeccak();
        position += reader.BytesRead;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeValueKeccakNonNull(scoped ref int position, out ValueHash256 keccak)
    {
        if ((uint)position + 1 + Hash256.Size > (uint)_data.Length || _data[position] != KeccakRlpPrefix)
        {
            ThrowInvalid("RLP byte string has another length than expected");
        }

        keccak = new ValueHash256(_data.Slice(position + 1, Hash256.Size));
        position += 1 + Hash256.Size;
    }

    /// <summary>Reads the prefix of the item at <paramref name="position"/> and checks that the item fits.</summary>
    /// <returns>The prefix length; zero for a single byte below <c>0x80</c>.</returns>
    /// <remarks>
    /// Reads the prefix in place, as constructing an <see cref="RlpReader"/> per call makes the inlining callers
    /// large enough that the JIT stops inlining and zeroes a large frame.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPrefix(int position, out int contentLength)
    {
        ReadOnlySpan<byte> data = _data;
        if ((uint)position >= (uint)data.Length) ThrowInvalid("RLP is truncated");

        int prefix = data[position];
        if (prefix < Rlp.EmptyByteArrayByte)
        {
            contentLength = 1;
            return 0;
        }

        if (prefix is > ShortStringMaxPrefix and < Rlp.EmptyListByte or > ShortListMaxPrefix)
        {
            // A branch or a leaf with a long value has a one- or two-byte length, so only longer lengths take the call.
            int lengthOfLength = prefix - (prefix < Rlp.EmptyListByte ? ShortStringMaxPrefix : ShortListMaxPrefix);
            if (lengthOfLength > 2 || position + 1 + lengthOfLength > data.Length) return ReadLongPrefix(data, position, prefix, out contentLength);

            contentLength = lengthOfLength == 1 ? data[position + 1] : (data[position + 1] << 8) | data[position + 2];
            if (position + 1 + lengthOfLength + contentLength > data.Length) ThrowInvalid("RLP is truncated");
            return 1 + lengthOfLength;
        }

        contentLength = prefix - (prefix < Rlp.EmptyListByte ? Rlp.EmptyByteArrayByte : Rlp.EmptyListByte);
        if (position + 1 + contentLength > data.Length) ThrowInvalid("RLP is truncated");
        return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLongItemLength(ReadOnlySpan<byte> data, int position, int prefix) =>
        ReadLongPrefix(data, position, prefix, out int contentLength) + contentLength;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadLongPrefix(ReadOnlySpan<byte> data, int position, int prefix, out int contentLength)
    {
        int lengthOfLength = prefix - (prefix < Rlp.EmptyListByte ? ShortStringMaxPrefix : ShortListMaxPrefix);
        if (lengthOfLength > sizeof(int)) ThrowInvalid("RLP length is too large");
        if (position + 1 + lengthOfLength > data.Length) ThrowInvalid("RLP is truncated");

        uint length = 0;
        foreach (byte b in data.Slice(position + 1, lengthOfLength))
        {
            length = (length << 8) | b;
        }

        if ((long)position + 1 + lengthOfLength + length > data.Length) ThrowInvalid("RLP is truncated");
        contentLength = (int)length;
        return 1 + lengthOfLength;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalid(string message) => throw new RlpReaderException(message);
}
