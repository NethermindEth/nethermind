// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

[SszVectorConverter<byte>]
public static class ByteSszVectorConverter
{
    public const int Length = sizeof(byte);
    public const bool PacksItems = true;

    public static byte FromSpan(ReadOnlySpan<byte> span) => span[0];

    public static void FromSpan(ReadOnlySpan<byte> span, Span<byte> values) => span[..values.Length].CopyTo(values);

    public static void ToSpan(Span<byte> span, byte value) => span[0] = value;

    public static void ToSpan(Span<byte> span, ReadOnlySpan<byte> values) => values.CopyTo(span);

    public static void Feed(ref Merkleizer merkleizer, byte value) => merkleizer.Feed(new UInt256(value));

    public static void MerkleizeVector(ReadOnlySpan<byte> values, ulong length, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeVector(values, Length, length, ToSpan, out root);

    public static void MerkleizeList(ReadOnlySpan<byte> values, ulong limit, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeList(values, Length, limit, ToSpan, out root);

    public static void MerkleizeProgressiveList(ReadOnlySpan<byte> values, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeProgressiveList(values, Length, ToSpan, out root);
}
