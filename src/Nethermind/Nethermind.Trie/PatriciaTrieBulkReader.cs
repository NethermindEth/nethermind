// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Sink that receives values read during a bulk Patricia trie read.
/// Implementations must be thread-safe when parallelism is enabled.
/// </summary>
/// <typeparam name="TSelf">The implementing struct type (for devirtualization).</typeparam>
public interface IPatriciaTrieBulkReaderSink<TSelf> where TSelf : IPatriciaTrieBulkReaderSink<TSelf>
{
    /// <summary>
    /// Called for each key that has been read from the trie.
    /// </summary>
    /// <param name="key">The original key.</param>
    /// <param name="idx">The index of this key in the original input span.</param>
    /// <param name="value">The raw value bytes from the trie, or empty if the key was not found.</param>
    void OnRead(in ValueHash256 key, int idx, ReadOnlySpan<byte> value);
}

/// <summary>
/// Bulk reader for Patricia tries. Sorts keys by nibble path and traverses the trie once,
/// fanning out at branch nodes to avoid redundant node resolutions.
/// Parallelizes at the top level when entry count reaches a threshold.
/// Mirrors the algorithm from <see cref="PatriciaTree.BulkSet"/>.
/// </summary>
public static class PatriciaTrieBulkReader
{
    private const int MinEntriesToParallelizeThreshold = PatriciaTree.MinEntriesToParallelizeThreshold;
    private const int FullBranch = (1 << TrieNode.BranchesCount) - 1;

    /// <summary>
    /// Entry for bulk reading. Carries the path and the original index in the input span.
    /// </summary>
    public readonly struct BulkReadEntry(in ValueHash256 path, int originalIndex) : IComparable<BulkReadEntry>
    {
        public readonly ValueHash256 Path = path;
        public readonly int OriginalIndex = originalIndex;

        public int CompareTo(BulkReadEntry entry) => Path.CompareTo(entry.Path);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetPathNibble(int index)
        {
            int offset = index / 2;
            Span<byte> theSpan = Path.BytesAsSpan;
            int b = theSpan[offset];

            return (index & 1) == 0
                ? (byte)((b & 0xf0) >> 4)
                : (byte)(b & 0x0f);
        }
    }

    private readonly record struct Context(BulkReadEntry[] OriginalEntriesArray, BulkReadEntry[] OriginalSortBufferArray);

    /// <summary>
    /// Read all <paramref name="keys"/> from the trie rooted at <paramref name="root"/>,
    /// reporting each result via <paramref name="reader"/>.
    /// The reader must be thread-safe as results may be reported from multiple threads in parallel.
    /// </summary>
    public static void BulkRead<TReader>(
        IScopedTrieStore trieStore,
        TrieNode? root,
        ReadOnlySpan<ValueHash256> keys,
        ref TReader reader)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        if (keys.Length == 0)
            return;

        using ArrayPoolListRef<BulkReadEntry> entries = new(keys.Length, keys.Length);
        using ArrayPoolListRef<BulkReadEntry> sortBuffer = new(keys.Length, keys.Length);

        for (int i = 0; i < keys.Length; i++)
        {
            entries[i] = new BulkReadEntry(in keys[i], i);
        }

        Context ctx = new()
        {
            OriginalEntriesArray = entries.UnsafeGetInternalArray(),
            OriginalSortBufferArray = sortBuffer.UnsafeGetInternalArray(),
        };

        TreePath path = TreePath.Empty;

        BulkReadRecursive(
            ctx,
            trieStore,
            entries.AsSpan(),
            sortBuffer.AsSpan(),
            ref path,
            root,
            0,
            ref reader);
    }

    private static void BulkReadRecursive<TReader>(
        in Context ctx,
        IScopedTrieStore trieStore,
        Span<BulkReadEntry> entries,
        Span<BulkReadEntry> sortBuffer,
        ref TreePath path,
        TrieNode? node,
        int flipCount,
        ref TReader reader)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        if (entries.Length == 1)
        {
            BulkReadOne(trieStore, in entries[0], ref path, node, ref reader);
            return;
        }

        if (node is null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
            }
            return;
        }

        node.ResolveNode(trieStore, path);

        if (node.IsLeaf || node.IsExtension)
        {
            HandleLeafOrExtension(ctx, trieStore, entries, sortBuffer, ref path, node, flipCount, ref reader);
            return;
        }

        // Branch node — partition entries by nibble and recurse
        if (path.Length >= 64)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
            }
            return;
        }

        Span<int> indexes = stackalloc int[TrieNode.BranchesCount];
        int nibMask = BucketSort16(entries, sortBuffer, path.Length, indexes);

        // After sort, sortBuffer has the sorted entries; swap
        flipCount++;
        Span<BulkReadEntry> sorted = sortBuffer;
        Span<BulkReadEntry> buffer = entries;

        // Parallel path: when enough entries and all 16 nibbles present
        if (entries.Length >= MinEntriesToParallelizeThreshold && nibMask == FullBranch)
        {
            BulkReadParallel(ctx, trieStore, sorted, buffer, ref path, node, nibMask, indexes, flipCount, reader);
            return;
        }

        // Sequential path
        TrieNode.ChildIterator childIterator = node.CreateChildIterator();
        path.AppendMut(0);

        while (nibMask != 0)
        {
            int nib = BitOperations.TrailingZeroCount(nibMask);
            nibMask &= nibMask - 1;

            int startRange = indexes[nib];
            int endRange = nibMask != 0 ? indexes[BitOperations.TrailingZeroCount(nibMask)] : sorted.Length;

            path.SetLast(nib);
            TrieNode? child = childIterator.GetChildWithChildPath(trieStore, ref path, nib);

            Span<BulkReadEntry> slice = sorted[startRange..endRange];
            Span<BulkReadEntry> bufSlice = buffer[startRange..endRange];

            if (slice.Length == 1)
            {
                BulkReadOne(trieStore, in slice[0], ref path, child, ref reader);
            }
            else
            {
                BulkReadRecursive(in ctx, trieStore, slice, bufSlice, ref path, child, flipCount, ref reader);
            }
        }

        path.TruncateOne();
    }

    private static void BulkReadParallel<TReader>(
        in Context ctx,
        IScopedTrieStore trieStore,
        Span<BulkReadEntry> sorted,
        Span<BulkReadEntry> buffer,
        ref TreePath path,
        TrieNode node,
        int nibMask,
        Span<int> indexes,
        int flipCount,
        TReader reader) // by value — each parallel worker gets a copy (struct holds refs to shared data)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        using ArrayPoolList<(
            int startIdx,
            int count,
            int nibble,
            TreePath childPath,
            TrieNode? child
        )> jobs = new(TrieNode.BranchesCount, TrieNode.BranchesCount);

        BulkReadEntry[] originalEntriesArray = (flipCount % 2 == 0) ? ctx.OriginalEntriesArray : ctx.OriginalSortBufferArray;
        BulkReadEntry[] originalBufferArray = (flipCount % 2 == 0) ? ctx.OriginalSortBufferArray : ctx.OriginalEntriesArray;
        TrieNode.ChildIterator childIterator = node.CreateChildIterator();

        while (nibMask != 0)
        {
            int nib = BitOperations.TrailingZeroCount(nibMask);
            nibMask &= nibMask - 1;
            int startRange = indexes[nib];
            int endRange = nibMask != 0 ? indexes[BitOperations.TrailingZeroCount(nibMask)] : sorted.Length;

            Span<BulkReadEntry> jobEntry = sorted.Slice(startRange, endRange - startRange);

            TreePath childPath = path.Append(nib);
            TrieNode? child = childIterator.GetChildWithChildPath(trieStore, ref childPath, nib);
            jobs[nib] = (GetSpanOffset(originalEntriesArray, jobEntry), jobEntry.Length, nib, childPath, child);
        }

        Context closureCtx = ctx;

        Parallel.For(0, TrieNode.BranchesCount, ParallelUnbalancedWork.DefaultOptions,
            (i) =>
            {
                (int startIdx, int count, int nib, TreePath childPath, TrieNode? child) = jobs[i];
                if (count == 0) return;

                Span<BulkReadEntry> jobEntries = originalEntriesArray.AsSpan(startIdx, count);
                Span<BulkReadEntry> bufferEntries = originalBufferArray.AsSpan(startIdx, count);

                TReader localReader = reader; // copy of the struct

                if (count == 1)
                {
                    BulkReadOne(trieStore, in jobEntries[0], ref childPath, child, ref localReader);
                }
                else
                {
                    BulkReadRecursive(
                        in closureCtx,
                        trieStore,
                        jobEntries,
                        bufferEntries,
                        ref childPath,
                        child,
                        flipCount,
                        ref localReader);
                }
            }
        );
    }

    private static void HandleLeafOrExtension<TReader>(
        in Context ctx,
        IScopedTrieStore trieStore,
        Span<BulkReadEntry> entries,
        Span<BulkReadEntry> sortBuffer,
        ref TreePath path,
        TrieNode node,
        int flipCount,
        ref TReader reader)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        ReadOnlySpan<byte> nodeKey = node.Key;

        if (node.IsLeaf)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (MatchesRemainingKey(in entries[i], path.Length, nodeKey))
                {
                    CappedArray<byte> val = node.Value;
                    reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, val.IsNull ? ReadOnlySpan<byte>.Empty : val.AsSpan());
                }
                else
                {
                    reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
                }
            }
        }
        else
        {
            // Extension: partition entries matching the prefix, recurse into child
            int matchCount = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (MatchesPrefix(in entries[i], path.Length, nodeKey))
                {
                    sortBuffer[matchCount++] = entries[i];
                }
                else
                {
                    reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
                }
            }

            if (matchCount > 0)
            {
                path.AppendMut(nodeKey);
                TrieNode? child = node.GetChildWithChildPath(trieStore, ref path, 0);

                Span<BulkReadEntry> matched = sortBuffer[..matchCount];
                Span<BulkReadEntry> scratch = entries[..matchCount];

                if (matchCount == 1)
                {
                    BulkReadOne(trieStore, in matched[0], ref path, child, ref reader);
                }
                else
                {
                    BulkReadRecursive(in ctx, trieStore, matched, scratch, ref path, child, flipCount, ref reader);
                }

                path.TruncateMut(path.Length - nodeKey.Length);
            }
        }
    }

    [SkipLocalsInit]
    private static void BulkReadOne<TReader>(
        IScopedTrieStore trieStore,
        in BulkReadEntry entry,
        ref TreePath path,
        TrieNode? node,
        ref TReader reader)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        int originalPathLength = path.Length;

        Span<byte> nibbles = stackalloc byte[64];
        Nibbles.BytesToNibbleBytes(entry.Path.BytesAsSpan, nibbles);
        Span<byte> remainingKey = nibbles[path.Length..];

        while (true)
        {
            if (node is null)
            {
                reader.OnRead(in entry.Path, entry.OriginalIndex, ReadOnlySpan<byte>.Empty);
                path.TruncateMut(originalPathLength);
                return;
            }

            node.ResolveNode(trieStore, path);

            if (node.IsLeaf || node.IsExtension)
            {
                int commonPrefixLength = remainingKey.CommonPrefixLength(node.Key);
                if (commonPrefixLength == node.Key!.Length)
                {
                    if (node.IsLeaf)
                    {
                        if (commonPrefixLength == remainingKey.Length)
                        {
                            CappedArray<byte> val = node.Value;
                            reader.OnRead(in entry.Path, entry.OriginalIndex, val.IsNull ? ReadOnlySpan<byte>.Empty : val.AsSpan());
                        }
                        else
                        {
                            reader.OnRead(in entry.Path, entry.OriginalIndex, ReadOnlySpan<byte>.Empty);
                        }
                        path.TruncateMut(originalPathLength);
                        return;
                    }

                    // Extension — continue to child
                    path.AppendMut(node.Key);
                    TrieNode? extensionChild = node.GetChildWithChildPath(trieStore, ref path, 0);
                    remainingKey = remainingKey[node.Key.Length..];
                    node = extensionChild;
                    continue;
                }

                // No match
                reader.OnRead(in entry.Path, entry.OriginalIndex, ReadOnlySpan<byte>.Empty);
                path.TruncateMut(originalPathLength);
                return;
            }

            // Branch node
            int nib = remainingKey[0];
            path.AppendMut(nib);
            TrieNode? child = node.GetChildWithChildPath(trieStore, ref path, nib);
            node = child;
            remainingKey = remainingKey[1..];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesRemainingKey(in BulkReadEntry entry, int pathLength, ReadOnlySpan<byte> nodeKey)
    {
        if (64 - pathLength != nodeKey.Length)
            return false;

        for (int i = 0; i < nodeKey.Length; i++)
        {
            if (entry.GetPathNibble(pathLength + i) != nodeKey[i])
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesPrefix(in BulkReadEntry entry, int pathLength, ReadOnlySpan<byte> prefix)
    {
        if (64 - pathLength < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (entry.GetPathNibble(pathLength + i) != prefix[i])
                return false;
        }

        return true;
    }

    internal static int BucketSort16(
        Span<BulkReadEntry> entries,
        Span<BulkReadEntry> sortTarget,
        int pathIndex,
        Span<int> indexes)
    {
        Span<int> counts = stackalloc int[TrieNode.BranchesCount];

        for (int i = 0; i < entries.Length; i++)
        {
            byte nib = entries[i].GetPathNibble(pathIndex);
            counts[nib]++;
        }

        int usedMask = 0;
        Span<int> starts = stackalloc int[TrieNode.BranchesCount];
        int total = 0;

        for (int nib = 0; nib < TrieNode.BranchesCount; nib++)
        {
            starts[nib] = total;
            total += counts[nib];

            if (counts[nib] != 0)
            {
                usedMask |= 1 << nib;
                indexes[nib] = starts[nib];
            }
        }

        for (int i = 0; i < entries.Length; i++)
        {
            int nib = entries[i].GetPathNibble(pathIndex);
            sortTarget[starts[nib]++] = entries[i];
        }

        return usedMask;
    }

    private static int GetSpanOffset(BulkReadEntry[] array, Span<BulkReadEntry> span)
    {
        ref BulkReadEntry spanRef = ref MemoryMarshal.GetReference(span);
        ref BulkReadEntry arrRef = ref MemoryMarshal.GetArrayDataReference(array);

        return (int)(Unsafe.ByteOffset(ref arrRef, ref spanRef) / Unsafe.SizeOf<BulkReadEntry>());
    }
}
