// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

[SszVectorConverter<int>]
public static class Int32SszVectorConverter
{
    public const int Length = sizeof(int);
    public const bool PacksItems = true;

    public static int FromSpan(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span);

    public static void FromSpan(ReadOnlySpan<byte> span, Span<int> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, int>(span).CopyTo(values);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, int value) => BinaryPrimitives.WriteInt32LittleEndian(span, value);

    public static void ToSpan(Span<byte> span, ReadOnlySpan<int> values)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(values).CopyTo(span);
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, int value) =>
        merkleizer.Feed(new UInt256(unchecked((uint)value)));

    public static void MerkleizeVector(ReadOnlySpan<int> values, ulong length, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeVector(values, Length, length, ToSpan, out root);

    public static void MerkleizeList(ReadOnlySpan<int> values, ulong limit, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeList(values, Length, limit, ToSpan, out root);

    public static void MerkleizeProgressiveList(ReadOnlySpan<int> values, out UInt256 root) =>
        PackedSszVectorConverterMerkle.MerkleizeProgressiveList(values, Length, ToSpan, out root);
}
