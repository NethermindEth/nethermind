// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Avalanche.Parity;

/// <summary>
/// Coreth storage-key transforms applied to a 32-byte key before it is hashed into the storage trie.
/// </summary>
/// <remarks>
/// Coreth partitions the storage trie key space into normal state storage and multi-coin storage using the
/// lowest bit of the first key byte. Normal keys clear that bit (<c>key[0] &amp;= 0xFE</c>) and multi-coin
/// coin-id keys set it (<c>coinID[0] |= 0x01</c>). The transform is applied <i>before</i> the keccak256 of
/// the key, so the two partitions can never collide. Source: Coreth <c>NormalizeStateKey</c> /
/// <c>NormalizeCoinID</c> (github.com/ava-labs/coreth, <c>core/state</c>).
/// </remarks>
public static class AvalancheStorageKey
{
    /// <summary>Lowest bit of the first byte; set marks multi-coin storage, cleared marks normal storage.</summary>
    public const byte PartitionBit = 0x01;

    /// <summary>
    /// Normalizes a normal state-storage key in place by clearing bit 0 of the first byte
    /// (<c>key[0] &amp;= 0xFE</c>), matching Coreth's <c>NormalizeStateKey</c>.
    /// </summary>
    /// <param name="key">A 32-byte storage key; mutated in place.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static void Normalize(Span<byte> key)
    {
        if (key.IsEmpty)
        {
            ThrowEmptyKey();
        }

        key[0] &= unchecked((byte)~PartitionBit);
    }

    /// <summary>
    /// Normalizes a multi-coin coin-id key in place by setting bit 0 of the first byte
    /// (<c>coinID[0] |= 0x01</c>), matching Coreth's <c>NormalizeCoinID</c>.
    /// </summary>
    /// <param name="coinId">A 32-byte coin-id key; mutated in place.</param>
    /// <exception cref="ArgumentException"><paramref name="coinId"/> is empty.</exception>
    public static void NormalizeCoinId(Span<byte> coinId)
    {
        if (coinId.IsEmpty)
        {
            ThrowEmptyKey();
        }

        coinId[0] |= PartitionBit;
    }

    /// <summary>Returns a normalized copy of a normal state-storage key without mutating the input.</summary>
    public static byte[] Normalized(ReadOnlySpan<byte> key)
    {
        byte[] copy = key.ToArray();
        Normalize(copy);
        return copy;
    }

    /// <summary>Returns a normalized copy of a multi-coin coin-id key without mutating the input.</summary>
    public static byte[] NormalizedCoinId(ReadOnlySpan<byte> coinId)
    {
        byte[] copy = coinId.ToArray();
        NormalizeCoinId(copy);
        return copy;
    }

    private static void ThrowEmptyKey() => throw new ArgumentException("Storage key must not be empty.", "key");
}
