// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Returns the SSZ root of a bitvector.</summary>
    public static void Merkleize(out UInt256 root, BitArray value) => MerkleizeBits(out root, value, 0);

    /// <summary>Returns the SSZ root of a bitlist with the given capacity in bits.</summary>
    public static void Merkleize(out UInt256 root, BitArray value, ulong limit)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((ulong)value.Length, limit, nameof(value));
        ulong chunkCount = limit / 256 + (limit % 256 == 0 ? 0UL : 1UL);
        MerkleizeBits(out root, value, chunkCount);
        MixIn(ref root, value.Length);
    }

    /// <summary>Returns the SSZ root of a progressive bitlist, including its bit length.</summary>
    public static void MerkleizeProgressive(out UInt256 root, BitArray value)
    {
        MerkleizeBits(out root, value, 0, progressive: true);
        MixIn(ref root, value.Length);
    }

    private static void MerkleizeBits(out UInt256 root, BitArray value, ulong chunkCount, bool progressive = false)
    {
        ReadOnlySpan<byte> bytes = CollectionsMarshal.AsBytes(value);
        int remainingBits = value.Length % 8;
        if (remainingBits == 0)
        {
            if (progressive)
            {
                MerkleizeProgressive(out root, bytes);
            }
            else
            {
                Merkleize(out root, bytes, chunkCount);
            }

            return;
        }

        // CollectionsMarshal exposes unspecified high bits; mask a copy without mutating the BitArray.
        int lastChunkStart = (bytes.Length - 1) / 32 * 32;
        Span<byte> lastChunk = stackalloc byte[32];
        lastChunk.Clear();
        bytes[lastChunkStart..].CopyTo(lastChunk);
        lastChunk[bytes.Length - lastChunkStart - 1] &= (byte)((1 << remainingBits) - 1);
        ReadOnlySpan<UInt256> chunks = MemoryMarshal.Cast<byte, UInt256>(bytes[..lastChunkStart]);
        ReadOnlySpan<UInt256> tail = MemoryMarshal.Cast<byte, UInt256>(lastChunk);
        if (progressive)
        {
            MerkleizeProgressive(out root, chunks, tail, 1);
        }
        else
        {
            Merkleize(out root, chunks, tail, chunkCount);
        }
    }
}
