// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core;
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

    private readonly ReadOnlySpan<byte> _data = data;

    public ReadOnlySpan<byte> Data => _data;

    /// <summary>Gets the prefix and payload lengths of the item at <paramref name="position"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength(int position)
    {
        RlpReader reader = new(_data[position..]);
        if (_data[position] >= Rlp.EmptyListByte)
        {
            RlpReader content = reader.EnterSequence<OffFlag>(out int itemLength);
            return (itemLength - content.Remaining, content.Remaining);
        }

        int contentLength = reader.ReadBytes<OffFlag>().Length;
        return (reader.BytesRead - contentLength, contentLength);
    }

    /// <summary>Gets the length of the item at <paramref name="position"/>, with its prefix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength(int position) => new RlpReader(_data[position..]).ReadEncodedItem<OffFlag>().Length;

    /// <summary>Moves <paramref name="position"/> past the prefix of the item there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipLength(scoped ref int position) => position += PeekPrefixAndContentLength(position).PrefixLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSequenceNext(int position) => _data[position] >= Rlp.EmptyListByte;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem(scoped ref int position) => position += PeekNextRlpLength(position);

    public void SkipItems(scoped ref int position, int count)
    {
        RlpReader reader = new(_data[position..]);
        for (int i = 0; i < count; i++)
        {
            reader.SkipItem<OffFlag>();
        }

        position += reader.BytesRead;
    }

    /// <summary>Moves <paramref name="position"/> past the prefix of the sequence there.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSequenceLength(scoped ref int position, out int contentLength)
    {
        RlpReader content = new RlpReader(_data[position..]).EnterSequence<OffFlag>(out int itemLength);
        contentLength = content.Remaining;
        position += itemLength - contentLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value)
    {
        RlpReader reader = new(_data[position..]);
        value = reader.ReadBytes<OffFlag>();
        position += reader.BytesRead;
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
        RlpReader reader = new(_data[position..]);
        keccak = reader.DecodeValueKeccakNonNull();
        position += reader.BytesRead;
    }
}
