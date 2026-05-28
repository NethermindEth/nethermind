// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Core;

[SszVectorConverter<UInt256>]
public static class UInt256SszVectorConverter
{
    public const int Length = 32;
    public const bool PacksItems = true;

    public static UInt256 FromSpan(ReadOnlySpan<byte> span) => new(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<UInt256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, UInt256 value) => value.ToLittleEndian(span);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<UInt256> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, UInt256 value) => merkleizer.Feed(value);

    public static void MerkleizeVector(ReadOnlySpan<UInt256> values, ulong length, out UInt256 root) =>
        Merkle.Merkleize(out root, values, length);

    public static void MerkleizeList(ReadOnlySpan<UInt256> values, ulong limit, out UInt256 root)
    {
        Merkle.Merkleize(out root, values, limit);
        Merkle.MixIn(ref root, values.Length);
    }

    public static void MerkleizeProgressiveList(ReadOnlySpan<UInt256> values, out UInt256 root)
    {
        Merkle.MerkleizeProgressive(out root, values);
        Merkle.MixIn(ref root, values.Length);
    }
}
