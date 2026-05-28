// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz.SszVectorConverters;

internal static class PackedSszVectorConverterMerkle
{
    private const int StackByteLimit = 256;

    internal delegate void EncodeItems<T>(Span<byte> span, ReadOnlySpan<T> values);

    internal static void MerkleizeVector<T>(
        ReadOnlySpan<T> values, int itemSize, ulong length, EncodeItems<T> encode, out UInt256 root)
        where T : struct
    {
        ulong chunkCount = (length * (ulong)itemSize + 31UL) / 32UL;
        if (BitConverter.IsLittleEndian)
        {
            Merkle.Merkleize(out root, MemoryMarshal.AsBytes(values), chunkCount);
            return;
        }

        MerkleizeEncodedCollection(values, itemSize, chunkCount, encode, out root);
    }

    internal static void MerkleizeList<T>(
        ReadOnlySpan<T> values, int itemSize, ulong limit, EncodeItems<T> encode, out UInt256 root)
        where T : struct
    {
        ulong chunkCount = (limit * (ulong)itemSize + 31UL) / 32UL;
        if (BitConverter.IsLittleEndian)
        {
            Merkle.Merkleize(out root, MemoryMarshal.AsBytes(values), chunkCount);
        }
        else
        {
            MerkleizeEncodedCollection(values, itemSize, chunkCount, encode, out root);
        }

        Merkle.MixIn(ref root, values.Length);
    }

    internal static void MerkleizeProgressiveList<T>(
        ReadOnlySpan<T> values, int itemSize, EncodeItems<T> encode, out UInt256 root)
        where T : struct
    {
        if (BitConverter.IsLittleEndian)
        {
            MerkleizeProgressiveBytes(MemoryMarshal.AsBytes(values), out root);
        }
        else
        {
            MerkleizeProgressiveEncodedCollection(values, itemSize, encode, out root);
        }

        Merkle.MixIn(ref root, values.Length);
    }

    private static void MerkleizeEncodedCollection<T>(
        ReadOnlySpan<T> values, int itemSize, ulong chunkCount, EncodeItems<T> encode, out UInt256 root)
    {
        int byteLength = checked(values.Length * itemSize);
        scoped Span<byte> bytes;
        byte[]? rented = null;
        if (byteLength <= StackByteLimit)
        {
            Span<byte> stack = stackalloc byte[StackByteLimit];
            bytes = stack[..byteLength];
        }
        else
        {
            rented = ArrayPool<byte>.Shared.Rent(byteLength);
            bytes = rented.AsSpan(0, byteLength);
        }

        try
        {
            Debug.Assert(bytes.Length == byteLength, "SSZ collection buffer length must match item count and item size.");
            encode(bytes, values);
            Merkle.Merkleize(out root, bytes, chunkCount);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void MerkleizeProgressiveEncodedCollection<T>(
        ReadOnlySpan<T> values, int itemSize, EncodeItems<T> encode, out UInt256 root)
    {
        int byteLength = checked(values.Length * itemSize);
        scoped Span<byte> bytes;
        byte[]? rented = null;
        if (byteLength <= StackByteLimit)
        {
            Span<byte> stack = stackalloc byte[StackByteLimit];
            bytes = stack[..byteLength];
        }
        else
        {
            rented = ArrayPool<byte>.Shared.Rent(byteLength);
            bytes = rented.AsSpan(0, byteLength);
        }

        try
        {
            Debug.Assert(bytes.Length == byteLength, "SSZ collection buffer length must match item count and item size.");
            encode(bytes, values);
            MerkleizeProgressiveBytes(bytes, out root);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void MerkleizeProgressiveBytes(ReadOnlySpan<byte> value, out UInt256 root)
    {
        if (value.Length is 0)
        {
            root = UInt256.Zero;
            return;
        }

        int chunkCount = (value.Length + 31) / 32;
        const int StackChunkLimit = 4;
        scoped Span<UInt256> chunks;
        UInt256[]? rented = null;
        if (chunkCount <= StackChunkLimit)
        {
            Span<UInt256> stack = stackalloc UInt256[StackChunkLimit];
            chunks = stack[..chunkCount];
        }
        else
        {
            rented = ArrayPool<UInt256>.Shared.Rent(chunkCount);
            chunks = rented.AsSpan(0, chunkCount);
        }

        try
        {
            chunks.Clear();
            int fullByteLength = value.Length / 32 * 32;
            if (fullByteLength > 0)
            {
                MemoryMarshal.Cast<byte, UInt256>(value[..fullByteLength]).CopyTo(chunks);
            }

            if (fullByteLength != value.Length)
            {
                Span<byte> lastChunk = stackalloc byte[32];
                lastChunk.Clear();
                value[fullByteLength..].CopyTo(lastChunk);
                chunks[chunkCount - 1] = new UInt256(lastChunk);
            }

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
}
