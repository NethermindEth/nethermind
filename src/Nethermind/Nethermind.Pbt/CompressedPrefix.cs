// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>A borrowed view of a compressed prefix's big-endian bit count and canonical bytes.</summary>
/// <remarks>The backing memory must remain alive and unchanged while the view is used. The default value represents an empty prefix.</remarks>
public readonly ref struct CompressedPrefix
{
    private readonly ReadOnlySpan<byte> _encoding;

    private CompressedPrefix(ReadOnlySpan<byte> encoding) => _encoding = encoding;

    /// <summary>Gets the number of meaningful prefix bits.</summary>
    public int BitCount => _encoding.IsEmpty ? 0 : BinaryPrimitives.ReadUInt16BigEndian(_encoding);

    /// <summary>Gets the borrowed, MSB-first prefix bytes without the count header.</summary>
    public ReadOnlySpan<byte> Bytes => _encoding.IsEmpty ? [] : _encoding[sizeof(ushort)..];

    internal static CompressedPrefix FromValidated(ReadOnlySpan<byte> encoding) => new(encoding);

    /// <summary>The number of leading prefix bits that match <paramref name="key"/> from bit <paramref name="keyOffset"/> on.</summary>
    internal int MatchingBits<TKey>(TKey key, int keyOffset) where TKey : unmanaged, IPbtKey<TKey>
    {
        if (BitCount == 0) return 0;

        ReadOnlySpan<byte> prefixBytes = Bytes;
        int available = key.BitLength - keyOffset;
        int count = Math.Min(BitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        if (keyBitOffset == 0)
            return Math.Min(count, PbtKeyOperations.FirstDifferingBit(prefixBytes, keyBytes[(keyOffset >> 3)..], 0));

        int index = 0;
        for (; index + 64 <= count; index += 64)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            ulong keyWord = (BinaryPrimitives.ReadUInt64BigEndian(keyBytes[keyByteIndex..]) << keyBitOffset)
                | (uint)(keyBytes[keyByteIndex + sizeof(ulong)] >> (8 - keyBitOffset));
            ulong difference = BinaryPrimitives.ReadUInt64BigEndian(prefixBytes[(index >> 3)..]) ^ keyWord;
            if (difference != 0) return index + BitOperations.LeadingZeroCount(difference);
        }
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && TrieUpdater.GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }
}
