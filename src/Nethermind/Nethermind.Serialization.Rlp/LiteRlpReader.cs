// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes RLP from a borrowed span and advances an explicit cursor.</summary>
/// <remarks>
/// The small instance methods pass the span and cursor by value to static decoding helpers.
/// Every forwarder is aggressively inlined so the caller's cursor can remain enregistered. This
/// is a readability refactor intended to preserve the existing helper cost, not a measured
/// performance claim. The cursor advances only after a helper returns successfully; no cursor or
/// memory backing is retained.
/// </remarks>
internal readonly ref struct LiteRlpReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public ReadOnlySpan<byte> Data => _data;

    /// <inheritdoc cref="RlpHelpers.PeekPrefixAndContentLength(ReadOnlySpan{byte}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength(int position)
        => RlpHelpers.PeekPrefixAndContentLength(_data, position);

    /// <inheritdoc cref="RlpHelpers.PeekNextRlpLength(ReadOnlySpan{byte}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength(int position)
        => RlpHelpers.PeekNextRlpLength(_data, position);

    /// <inheritdoc cref="RlpHelpers.CountItems(ReadOnlySpan{byte}, int, int, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountItems(int position, int end, int maxSearch)
        => RlpHelpers.CountItems(_data, position, end, maxSearch);

    /// <inheritdoc cref="RlpHelpers.SkipLength(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipLength(scoped ref int position)
        => position = RlpHelpers.SkipLength(_data, position);

    /// <inheritdoc cref="RlpHelpers.DecodeULong(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong DecodeULong(scoped ref int position)
    {
        (position, ulong value) = RlpHelpers.DecodeULong(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodePositiveInt(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodePositiveInt(scoped ref int position)
    {
        (position, int value) = RlpHelpers.DecodePositiveInt(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeKeccak(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256 DecodeKeccak(scoped ref int position)
    {
        (position, Hash256 value) = RlpHelpers.DecodeKeccak(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeKeccakOrNull(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256? DecodeKeccakOrNull(scoped ref int position)
    {
        (position, Hash256? value) = RlpHelpers.DecodeKeccakOrNull(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeAddressOrNull(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address? DecodeAddressOrNull(scoped ref int position)
    {
        (position, Address? value) = RlpHelpers.DecodeAddressOrNull(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeBloomOrNull(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bloom? DecodeBloomOrNull(scoped ref int position)
    {
        (position, Bloom? value) = RlpHelpers.DecodeBloomOrNull(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeBloomNonNull(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bloom DecodeBloomNonNull(scoped ref int position)
    {
        (position, Bloom value) = RlpHelpers.DecodeBloomNonNull(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeString(ReadOnlySpan{byte}, int, RlpLimit?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string DecodeString(scoped ref int position)
    {
        (position, string value) = RlpHelpers.DecodeString(_data, position);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeString(ReadOnlySpan{byte}, int, RlpLimit?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string DecodeString(scoped ref int position, RlpLimit limit)
    {
        (position, string value) = RlpHelpers.DecodeString(_data, position, limit);
        return value;
    }

    /// <inheritdoc cref="RlpHelpers.IsSequenceNext(ReadOnlySpan{byte}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSequenceNext(int position)
        => RlpHelpers.IsSequenceNext(_data, position);

    /// <inheritdoc cref="RlpHelpers.SkipItem(ReadOnlySpan{byte}, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem(scoped ref int position)
        => position = RlpHelpers.SkipItem(_data, position);

    /// <inheritdoc cref="RlpHelpers.SkipItems(ReadOnlySpan{byte}, int, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItems(scoped ref int position, int count)
        => position = RlpHelpers.SkipItems(_data, position, count);

    /// <inheritdoc cref="RlpHelpers.ReadSequenceLength(ReadOnlySpan{byte}, int, out int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSequenceLength(scoped ref int position, out int contentLength)
        => position = RlpHelpers.ReadSequenceLength(_data, position, out contentLength);

    /// <inheritdoc cref="RlpHelpers.ReadPrefixAndContentLength(ReadOnlySpan{byte}, int, out int, out int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadPrefixAndContentLength(scoped ref int position, out int prefixLength, out int contentLength)
        => position = RlpHelpers.ReadPrefixAndContentLength(_data, position, out prefixLength, out contentLength);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArraySpan(ReadOnlySpan{byte}, int, out ReadOnlySpan{byte}, RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value)
        => position = RlpHelpers.DecodeByteArraySpan(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArraySpan(ReadOnlySpan{byte}, int, out ReadOnlySpan{byte}, RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value, RlpLimit? limit)
        => position = RlpHelpers.DecodeByteArraySpan(_data, position, out value, limit);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArraySpan(ReadOnlySpan{byte}, int, out ReadOnlySpan{byte}, RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArraySpan(scoped ref int position, out ReadOnlySpan<byte> value, RlpLimit? limit, int size)
        => position = RlpHelpers.DecodeByteArraySpan(_data, position, out value, limit, size);

    /// <inheritdoc cref="RlpHelpers.DecodeULong(ReadOnlySpan{byte}, int, out ulong)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeULong(scoped ref int position, out ulong value)
        => position = RlpHelpers.DecodeULong(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeUInt(ReadOnlySpan{byte}, int, out uint)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeUInt(scoped ref int position, out uint value)
        => position = RlpHelpers.DecodeUInt(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodePositiveInt(ReadOnlySpan{byte}, int, out int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodePositiveInt(scoped ref int position, out int value)
        => position = RlpHelpers.DecodePositiveInt(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodePositiveLong(ReadOnlySpan{byte}, int, out long)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodePositiveLong(scoped ref int position, out long value)
        => position = RlpHelpers.DecodePositiveLong(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeUShort(ReadOnlySpan{byte}, int, out ushort)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeUShort(scoped ref int position, out ushort value)
        => position = RlpHelpers.DecodeUShort(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeByte(ReadOnlySpan{byte}, int, out byte)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByte(scoped ref int position, out byte value)
        => position = RlpHelpers.DecodeByte(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeUInt256(ReadOnlySpan{byte}, int, out UInt256, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeUInt256(scoped ref int position, out UInt256 value)
        => position = RlpHelpers.DecodeUInt256(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeUInt256(ReadOnlySpan{byte}, int, out UInt256, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeUInt256(scoped ref int position, out UInt256 value, int length)
        => position = RlpHelpers.DecodeUInt256(_data, position, out value, length);

    /// <inheritdoc cref="RlpHelpers.DecodeEvmWord(ReadOnlySpan{byte}, int, out EvmWord)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeEvmWord(scoped ref int position, out EvmWord value)
        => position = RlpHelpers.DecodeEvmWord(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeKeccakOrNull(ReadOnlySpan{byte}, int, out Hash256?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeKeccakOrNull(scoped ref int position, out Hash256? keccak)
        => position = RlpHelpers.DecodeKeccakOrNull(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.DecodeValueKeccakOrNull(ReadOnlySpan{byte}, int, out ValueHash256?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeValueKeccakOrNull(scoped ref int position, out ValueHash256? keccak)
        => position = RlpHelpers.DecodeValueKeccakOrNull(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.TryDecodeValueKeccak(ReadOnlySpan{byte}, int, out ValueHash256, out bool)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDecodeValueKeccak(scoped ref int position, out ValueHash256 keccak)
    {
        position = RlpHelpers.TryDecodeValueKeccak(_data, position, out keccak, out bool hasValue);
        return hasValue;
    }

    /// <inheritdoc cref="RlpHelpers.DecodeAddress(ReadOnlySpan{byte}, int, out Address)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeAddress(scoped ref int position, out Address address)
        => position = RlpHelpers.DecodeAddress(_data, position, out address);

    /// <inheritdoc cref="RlpHelpers.DecodeBloom(ReadOnlySpan{byte}, int, out Bloom)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeBloom(scoped ref int position, out Bloom bloom)
        => position = RlpHelpers.DecodeBloom(_data, position, out bloom);

    /// <inheritdoc cref="RlpHelpers.DecodeBloomSpan(ReadOnlySpan{byte}, int, out ReadOnlySpan{byte})" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeBloomSpan(scoped ref int position, out ReadOnlySpan<byte> bloomBytes)
        => position = RlpHelpers.DecodeBloomSpan(_data, position, out bloomBytes);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArray(ReadOnlySpan{byte}, int, out byte[], RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArray(scoped ref int position, out byte[] value)
        => position = RlpHelpers.DecodeByteArray(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArray(ReadOnlySpan{byte}, int, out byte[], RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArray(scoped ref int position, out byte[] value, RlpLimit? limit)
        => position = RlpHelpers.DecodeByteArray(_data, position, out value, limit);

    /// <inheritdoc cref="RlpHelpers.DecodeByteArray(ReadOnlySpan{byte}, int, out byte[], RlpLimit?, int)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeByteArray(scoped ref int position, out byte[] value, RlpLimit? limit, int size)
        => position = RlpHelpers.DecodeByteArray(_data, position, out value, limit, size);

    /// <inheritdoc cref="RlpHelpers.DecodeBool(ReadOnlySpan{byte}, int, out bool)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeBool(scoped ref int position, out bool value)
        => position = RlpHelpers.DecodeBool(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeZeroPrefixKeccakNonNull(ReadOnlySpan{byte}, int, out Hash256)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeZeroPrefixKeccakNonNull(scoped ref int position, out Hash256 keccak)
        => position = RlpHelpers.DecodeZeroPrefixKeccakNonNull(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.DecodeZeroPrefixKeccak(ReadOnlySpan{byte}, int, out Hash256?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeZeroPrefixKeccak(scoped ref int position, out Hash256? keccak)
        => position = RlpHelpers.DecodeZeroPrefixKeccak(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.DecodeKeccak(ReadOnlySpan{byte}, int, out Hash256)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeKeccak(scoped ref int position, out Hash256 keccak)
        => position = RlpHelpers.DecodeKeccak(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.DecodeValueKeccakNonNull(ReadOnlySpan{byte}, int, out ValueHash256)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeValueKeccakNonNull(scoped ref int position, out ValueHash256 keccak)
        => position = RlpHelpers.DecodeValueKeccakNonNull(_data, position, out keccak);

    /// <inheritdoc cref="RlpHelpers.DecodeString(ReadOnlySpan{byte}, int, out string, RlpLimit?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeString(scoped ref int position, out string value)
        => position = RlpHelpers.DecodeString(_data, position, out value);

    /// <inheritdoc cref="RlpHelpers.DecodeString(ReadOnlySpan{byte}, int, out string, RlpLimit?)" path="/summary"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecodeString(scoped ref int position, out string value, RlpLimit? limit)
        => position = RlpHelpers.DecodeString(_data, position, out value, limit);
}
