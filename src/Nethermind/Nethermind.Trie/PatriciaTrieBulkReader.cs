// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Sink that receives values read during a bulk Patricia trie read.
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
/// Mirrors the algorithm from <see cref="PatriciaTree.BulkSet"/>.
/// </summary>
public static class PatriciaTrieBulkReader
{
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

    /// <summary>
    /// Read all <paramref name="keys"/> from the trie rooted at <paramref name="root"/>,
    /// reporting each result via <paramref name="reader"/>.
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

        TreePath path = TreePath.Empty;

        BulkReadRecursive(
            trieStore,
            entries.AsSpan(),
            sortBuffer.AsSpan(),
            ref path,
            root,
            ref reader);
    }

    private static void BulkReadRecursive<TReader>(
        IScopedTrieStore trieStore,
        Span<BulkReadEntry> entries,
        Span<BulkReadEntry> sortBuffer,
        ref TreePath path,
        TrieNode? node,
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
            // All entries miss — report empty for each
            for (int i = 0; i < entries.Length; i++)
            {
                reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
            }
            return;
        }

        node.ResolveNode(trieStore, path);

        if (node.IsLeaf || node.IsExtension)
        {
            HandleLeafOrExtension(trieStore, entries, sortBuffer, ref path, node, ref reader);
            return;
        }

        // Branch node — partition entries by nibble and recurse
        if (path.Length >= 64)
        {
            // Should not happen with valid data
            for (int i = 0; i < entries.Length; i++)
            {
                reader.OnRead(in entries[i].Path, entries[i].OriginalIndex, ReadOnlySpan<byte>.Empty);
            }
            return;
        }

        Span<int> indexes = stackalloc int[TrieNode.BranchesCount];
        int nibMask = BucketSort16(entries, sortBuffer, path.Length, indexes);

        // After sort, sortBuffer has the sorted entries; swap
        Span<BulkReadEntry> sorted = sortBuffer;
        Span<BulkReadEntry> buffer = entries;

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
                BulkReadRecursive(trieStore, slice, bufSlice, ref path, child, ref reader);
            }
        }

        path.TruncateOne();
    }

    private static void HandleLeafOrExtension<TReader>(
        IScopedTrieStore trieStore,
        Span<BulkReadEntry> entries,
        Span<BulkReadEntry> sortBuffer,
        ref TreePath path,
        TrieNode node,
        ref TReader reader)
        where TReader : struct, IPatriciaTrieBulkReaderSink<TReader>
    {
        ReadOnlySpan<byte> nodeKey = node.Key;

        if (node.IsLeaf)
        {
            // Leaf: only matches if remaining nibbles == node.Key for an entry
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
            // Extension: check if entries share the extension prefix, then recurse into child
            // Partition entries: those that match the extension prefix continue, others miss
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
                // Reuse entries span as scratch for the next level
                Span<BulkReadEntry> scratch = entries[..matchCount];

                if (matchCount == 1)
                {
                    BulkReadOne(trieStore, in matched[0], ref path, child, ref reader);
                }
                else
                {
                    BulkReadRecursive(trieStore, matched, scratch, ref path, child, ref reader);
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

    /// <summary>
    /// Check if entry nibbles starting at <paramref name="pathLength"/> match <paramref name="nodeKey"/> exactly
    /// (for leaf nodes, remaining path must equal node key).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesRemainingKey(in BulkReadEntry entry, int pathLength, ReadOnlySpan<byte> nodeKey)
    {
        // Remaining nibbles must be exactly nodeKey.Length, and each nibble must match
        if (64 - pathLength != nodeKey.Length)
            return false;

        for (int i = 0; i < nodeKey.Length; i++)
        {
            if (entry.GetPathNibble(pathLength + i) != nodeKey[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if entry nibbles starting at <paramref name="pathLength"/> start with <paramref name="prefix"/>.
    /// </summary>
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

    /// <summary>
    /// Bucket sort entries by nibble at <paramref name="pathIndex"/>.
    /// Output goes to <paramref name="sortTarget"/>. Returns a bitmask of used nibbles.
    /// </summary>
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
}
