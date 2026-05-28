// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

[SszVectorConverter<UInt128>]
public static class UInt128SszVectorConverter
{
    public const int Length = 16;
    public const bool PacksItems = true;

    public static UInt128 FromSpan(ReadOnlySpan<byte> span)
    {
        ulong lower = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
        ulong upper = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8));
        return new UInt128(upper, lower);
    }

    public static void FromSpan(ReadOnlySpan<byte> span, Span<UInt128> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = FromSpan(span.Slice(i * Length, Length));
        }
    }

    public static void ToSpan(Span<byte> span, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(span[..8], (ulong)(value & ulong.MaxValue));
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8, 8), (ulong)(value >> 64));
    }

    public static void ToSpan(Span<byte> span, ReadOnlySpan<UInt128> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ToSpan(span.Slice(i * Length, Length), values[i]);
        }
    }

    public static void Feed(ref Merkleizer merkleizer, UInt128 value) =>
        merkleizer.Feed(new UInt256((ulong)(value & ulong.MaxValue), (ulong)(value >> 64)));

    public static void MerkleizeVector(ReadOnlySpan<UInt128> values, ulong length, out UInt256 root)
    {
        ulong chunkCount = (length * Length + 31UL) / 32UL;
        MerkleizeCollection(values, chunkCount, out root);
    }

    public static void MerkleizeList(ReadOnlySpan<UInt128> values, ulong limit, out UInt256 root)
    {
        ulong chunkCount = (limit * Length + 31UL) / 32UL;
        MerkleizeCollection(values, chunkCount, out root);
        Merkle.MixIn(ref root, values.Length);
    }

    public static void MerkleizeProgressiveList(ReadOnlySpan<UInt128> values, out UInt256 root)
    {
        MerkleizeProgressiveCollection(values, out root);
        Merkle.MixIn(ref root, values.Length);
    }

    private static void MerkleizeCollection(ReadOnlySpan<UInt128> values, ulong chunkCount, out UInt256 root)
    {
        int chunkLength = (values.Length + 1) / 2;
        UInt256[]? rented = null;
        scoped Span<UInt256> chunks = chunkLength <= 4
            ? stackalloc UInt256[4]
            : (rented = ArrayPool<UInt256>.Shared.Rent(chunkLength));

        chunks = chunks[..chunkLength];
        try
        {
            FillChunks(values, chunks);
            Merkle.Merkleize(out root, chunks, chunkCount);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<UInt256>.Shared.Return(rented);
            }
        }
    }

    private static void MerkleizeProgressiveCollection(ReadOnlySpan<UInt128> values, out UInt256 root)
    {
        int chunkLength = (values.Length + 1) / 2;
        UInt256[]? rented = null;
        scoped Span<UInt256> chunks = chunkLength <= 4
            ? stackalloc UInt256[4]
            : (rented = ArrayPool<UInt256>.Shared.Rent(chunkLength));

        chunks = chunks[..chunkLength];
        try
        {
            FillChunks(values, chunks);
            Merkle.MerkleizeProgressive(out root, chunks);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<UInt256>.Shared.Return(rented);
            }
        }
    }

    private static void FillChunks(ReadOnlySpan<UInt128> values, Span<UInt256> chunks)
    {
        chunks.Clear();
        for (int i = 0; i < values.Length; i++)
        {
            UInt128 value = values[i];
            ref UInt256 chunk = ref chunks[i / 2];
            if ((i & 1) == 0)
            {
                chunk = new UInt256((ulong)(value & ulong.MaxValue), (ulong)(value >> 64));
            }
            else
            {
                chunk = new UInt256(chunk.u0, chunk.u1, (ulong)(value & ulong.MaxValue), (ulong)(value >> 64));
            }
        }
    }
}
