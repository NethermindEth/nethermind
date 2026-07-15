// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

/// <summary>
/// A 31-byte EIP-8297 stem: a 4-bit zone identifier followed by 244 bits of key material
/// (non-storage zones) or 1 storage bit + 60-bit address prefix + 187-bit suffix (storage zones).
/// </summary>
/// <remarks>
/// Backed by a <see cref="ValueHash256"/> whose last byte is always zero, inheriting its
/// equality, comparison and hashing. Bits are indexed MSB-first: bit 0 is the most significant
/// bit of byte 0, matching the EIP's <c>_bytes_to_bits</c> traversal order.
/// </remarks>
public readonly record struct Stem
{
    public const int Length = 31;
    public const int LengthInBits = Length * 8;

    private readonly ValueHash256 _bytes;

    public Stem(ReadOnlySpan<byte> stem)
    {
        if (stem.Length != Length) throw new ArgumentException($"Stem must be {Length} bytes", nameof(stem));
        Span<byte> padded = stackalloc byte[32];
        stem.CopyTo(padded);
        _bytes = new ValueHash256(padded);
    }

    public ReadOnlySpan<byte> Bytes => _bytes.Bytes[..Length];

    public int Zone => Bytes[0] >> 4;

    public bool IsStorageZone => (Bytes[0] & 0x80) != 0;

    /// <summary>Gets stem bit <paramref name="index"/> in MSB-first order.</summary>
    public int GetBit(int index) => (Bytes[index >> 3] >> (7 - (index & 7))) & 1;

    /// <summary>
    /// Returns the first bit index at or after <paramref name="fromBit"/> where this stem differs
    /// from <paramref name="other"/>, or -1 if they agree on all remaining bits.
    /// </summary>
    public int FirstDifferingBit(in Stem other, int fromBit)
    {
        ReadOnlySpan<byte> a = Bytes;
        ReadOnlySpan<byte> b = other.Bytes;
        for (int byteIndex = fromBit >> 3; byteIndex < Length; byteIndex++)
        {
            int diff = a[byteIndex] ^ b[byteIndex];
            if (byteIndex == fromBit >> 3) diff &= 0xFF >> (fromBit & 7);
            if (diff != 0) return (byteIndex << 3) + BitOperations.LeadingZeroCount((uint)diff) - 24;
        }

        return -1;
    }

    public override string ToString() => Bytes.ToHexString();
}
