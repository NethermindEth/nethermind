// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Encodes a batch of elements into its exact-size SSZ destination.</summary>
    public delegate void EncodeItems<T>(Span<byte> span, ReadOnlySpan<T> items);

    private static ulong GetSszChunkCount(ulong count, int itemSize) =>
        checked(count / 32 * (ulong)itemSize + (count % 32 * (ulong)itemSize + 31) / 32);

    private static void MerkleizeBasicCollectionWithConverter<T>(
        ReadOnlySpan<T> values, int itemSize, ulong chunkCount, EncodeItems<T> encode, out UInt256 root)
    {
        // Basic SSZ widths divide a chunk. Encode bounded batches directly into the tree.
        if (itemSize <= 32 && (itemSize & (itemSize - 1)) == 0)
        {
            Span<byte> batch = stackalloc byte[256];
            Merkleizer merkleizer = new(stackalloc UInt256[NextPowerOfTwoExponent(chunkCount) + 1]);
            int itemsPerBatch = batch.Length / itemSize;
            while (!values.IsEmpty)
            {
                int count = Math.Min(values.Length, itemsPerBatch);
                int encodedLength = count * itemSize;
                encode(batch[..encodedLength], values[..count]);
                int paddedLength = (encodedLength + 31) / 32 * 32;
                batch[encodedLength..paddedLength].Clear();
                foreach (UInt256 chunk in MemoryMarshal.Cast<byte, UInt256>(batch[..paddedLength]))
                {
                    merkleizer.Feed(chunk);
                }
                values = values[count..];
            }
            merkleizer.CalculateRoot(out root);
            return;
        }

        const int StackByteLimit = 256;
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
            Merkleize(out root, bytes, chunkCount);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Calculates a basic vector root using a batch encoder.</summary>
    public static void MerkleizeBasicVectorWithConverter<T>(
        ReadOnlySpan<T> values, int itemSize, ulong length, EncodeItems<T> encode, out UInt256 root)
    {
        ulong chunkCount = GetSszChunkCount(length, itemSize);
        MerkleizeBasicCollectionWithConverter(values, itemSize, chunkCount, encode, out root);
    }

    /// <summary>Calculates a bounded basic list root using a batch encoder.</summary>
    public static void MerkleizeBasicListWithConverter<T>(
        ReadOnlySpan<T> values, int itemSize, ulong limit, EncodeItems<T> encode, out UInt256 root)
    {
        ulong chunkCount = GetSszChunkCount(limit, itemSize);
        MerkleizeBasicCollectionWithConverter(values, itemSize, chunkCount, encode, out root);
        MixIn(ref root, values.Length);
    }

    /// <summary>Calculates a progressive basic list root using a batch encoder.</summary>
    public static void MerkleizeProgressiveBasicListWithConverter<T>(
        ReadOnlySpan<T> values, int itemSize, EncodeItems<T> encode, out UInt256 root)
    {
        if (itemSize <= 32 && (itemSize & (itemSize - 1)) == 0)
        {
            Span<UInt256> subtrees = stackalloc UInt256[17];
            int subtreeCount = 0;
            int offset = 0;
            ulong capacity = 1;
            while (offset < values.Length)
            {
                int count = (int)Math.Min((ulong)(values.Length - offset), capacity * (ulong)(32 / itemSize));
                MerkleizeBasicCollectionWithConverter(values.Slice(offset, count), itemSize, capacity, encode, out subtrees[subtreeCount++]);
                offset += count;
                capacity *= 4;
            }
            MerkleizeProgressiveSubtrees(subtrees[..subtreeCount], out root);
            MixIn(ref root, values.Length);
            return;
        }

        const int StackByteLimit = 256;
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
            MerkleizeProgressive(out root, bytes);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
        MixIn(ref root, values.Length);
    }

    /// <summary>Calculates a progressive bitlist root; null represents an empty list.</summary>
    public static void MerkleizeProgressiveBitList(BitArray? value, out UInt256 root)
    {
        if (value is null)
        {
            root = UInt256.Zero;
            MixIn(ref root, 0);
            return;
        }
        MerkleizeProgressive(out root, value);
    }

    /// <summary>Feeds one element root into a Merkle tree.</summary>
    public delegate void FeedItem<T>(ref Merkleizer merkleizer, T item);

    private static void MerkleizeCompositeCollectionWithConverter<T>(
        ReadOnlySpan<T> items, ulong limit, FeedItem<T> feed, out UInt256 root)
    {
        Merkleizer merkleizer = new(stackalloc UInt256[NextPowerOfTwoExponent(limit) + 1]);
        foreach (T item in items)
        {
            feed(ref merkleizer, item);
        }
        merkleizer.CalculateRoot(out root);
    }

    /// <summary>Calculates a composite vector root using an element feeder.</summary>
    public static void MerkleizeCompositeVectorWithConverter<T>(ReadOnlySpan<T> items, ulong length, FeedItem<T> feed, out UInt256 root)
    {
        Debug.Assert((ulong)items.Length == length, "Vector items count must equal declared length");
        MerkleizeCompositeCollectionWithConverter(items, length, feed, out root);
    }

    /// <summary>Calculates a bounded composite list root using an element feeder.</summary>
    public static void MerkleizeCompositeListWithConverter<T>(ReadOnlySpan<T> items, ulong limit, FeedItem<T> feed, out UInt256 root)
    {
        MerkleizeCompositeCollectionWithConverter(items, limit, feed, out root);
        MixIn(ref root, items.Length);
    }

    /// <summary>Calculates a progressive composite list root using an element feeder.</summary>
    public static void MerkleizeCompositeProgressiveListWithConverter<T>(ReadOnlySpan<T> items, FeedItem<T> feed, out UInt256 root)
    {
        // Progressive subtrees contain 1, 4, 16, ... leaves; only their roots need retaining.
        Span<UInt256> subtrees = stackalloc UInt256[17];
        Span<UInt256> scratch = stackalloc UInt256[33];
        int subtreeCount = 0;
        int offset = 0;
        ulong capacity = 1;
        while (offset < items.Length)
        {
            int count = (int)Math.Min((ulong)(items.Length - offset), capacity);
            Merkleizer merkleizer = new(scratch[..(NextPowerOfTwoExponent(capacity) + 1)]);
            foreach (T item in items.Slice(offset, count)) feed(ref merkleizer, item);
            merkleizer.CalculateRoot(out subtrees[subtreeCount++]);
            offset += count;
            capacity *= 4;
        }
        MerkleizeProgressiveSubtrees(subtrees[..subtreeCount], out root);
        MixIn(ref root, items.Length);
    }

    /// <summary>Combines successive progressive subtree roots into a progressive Merkle root.</summary>
    public static void MerkleizeProgressiveSubtrees(ReadOnlySpan<UInt256> subtrees, out UInt256 root)
    {
        Span<UInt256> scratch = stackalloc UInt256[2];
        root = UInt256.Zero;
        for (int i = subtrees.Length - 1; i >= 0; i--)
        {
            Merkleizer branch = new(scratch[..2]);
            branch.Feed(subtrees[i]);
            branch.Feed(root);
            branch.CalculateRoot(out root);
        }
    }

    /// <summary>Calculates one element root using an element feeder.</summary>
    public static void MerkleizeWithConverter<T>(T value, FeedItem<T> feed, out UInt256 root)
    {
        Span<UInt256> chunk = stackalloc UInt256[1];
        Merkleizer merkleizer = new(chunk);
        feed(ref merkleizer, value);
        merkleizer.CalculateRoot(out root);
    }
}
