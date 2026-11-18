// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// Finds the key ranges below a boundary branch or extension that a snap commit persists which hold nothing at the
/// verified root: the empty children of a branch, the paths beside the key of an extension, and the paths beside the key
/// of a leaf child.
/// </summary>
internal static class SnapBoundaryCleaner
{
    internal interface IRangeDeleter
    {
        void DeleteRange(in ValueHash256 from, in ValueHash256 to);
        void DeleteNode(in TreePath path);

        /// <summary>The RLP of the node stored at <paramref name="path"/>, or null.</summary>
        byte[]? LoadNode(in TreePath path);
    }

    internal static void DeleteOutsideCompleteTree<TDeleter>(TreePath path, TrieNode? node, TDeleter deleter,
        in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) where TDeleter : IRangeDeleter
    {
        if (path.ToUpperBoundPath() < cleanupStart || path.ToLowerBoundPath() > cleanupEnd || node is null) return;
        if (node.IsLeaf)
        {
            DeleteBesideKey(path, node.Key!, deleter, cleanupStart, cleanupEnd);
            DeleteCompressedPaths(path, node, deleter, cleanupStart, cleanupEnd);
            return;
        }

        if (!node.IsExtension && !node.IsBranch) return;
        DeleteOutsideChildren(path, node, deleter, cleanupStart, cleanupEnd, cleanChildKeys: false);
        if (node.IsExtension)
        {
            TreePath parent = path;
            TrieNode? child = node.GetChild(NullTrieNodeResolver.Instance, ref parent, 0);
            DeleteOutsideCompleteTree(path.Append(node.Key!), child, deleter, cleanupStart, cleanupEnd);
        }
        else if (node.IsBranch)
        {
            for (int i = 0; i < 16; i++)
            {
                TreePath parent = path;
                TrieNode? child = node.GetChild(NullTrieNodeResolver.Instance, ref parent, i);
                if (child is not null)
                    DeleteOutsideCompleteTree(path.Append(i), child, deleter, cleanupStart, cleanupEnd);
            }
        }
    }

    public static void DeleteOutsideChildren<TDeleter>(in TreePath path, TrieNode node, TDeleter deleter, in ValueHash256 cleanupStart,
        in ValueHash256 cleanupEnd, bool cleanChildKeys = true) where TDeleter : IRangeDeleter
    {
        if (node.IsExtension)
        {
            DeleteBesideKey(path, node.Key!, deleter, cleanupStart, cleanupEnd);
            DeleteCompressedPaths(path, node, deleter, cleanupStart, cleanupEnd);
            return;
        }

        int runStart = -1;
        for (int i = 0; i <= 16; i++)
        {
            if (i < 16 && node.IsChildNull(i))
            {
                if (runStart < 0) runStart = i;
                continue;
            }

            if (runStart >= 0)
            {
                DeleteWithinBounds(deleter, path.Append(runStart).ToLowerBoundPath(), path.Append(i - 1).ToUpperBoundPath(), cleanupStart, cleanupEnd);
                runStart = -1;
            }

            if (cleanChildKeys && i < 16) DeleteBesideChildKey(path, node, i, deleter, cleanupStart, cleanupEnd);
        }
    }

    /// <remarks>
    /// A leaf child is the only key in the subtree of its path, and an extension child holds keys only under its key, so an
    /// older root that had more keys there left only stale data beside that key. A child known only by its hash is the
    /// node stored at its path: a persisted node is written only when each child is already stored with its hash, so the
    /// type is read from there. A branch child needs nothing here: it was cleaned when it was persisted.
    /// </remarks>
    private static void DeleteBesideChildKey<TDeleter>(TreePath path, TrieNode node, int childIndex, TDeleter deleter, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) where TDeleter : IRangeDeleter
    {
        TreePath childPath = path;
        TrieNode? child = node.GetChild(NullTrieNodeResolver.Instance, ref childPath, childIndex);
        childPath = path.Append(childIndex);
        if (child is { NodeType: NodeType.Unknown, Keccak: { } hash })
        {
            child = deleter.LoadNode(childPath) is { } rlp && ValueKeccak.Compute(rlp) == hash.ValueHash256
                ? Resolve(rlp, childPath)
                : null;
        }

        if (child is { IsLeaf: true } or { IsExtension: true })
        {
            DeleteBesideKey(childPath, child.Key!, deleter, cleanupStart, cleanupEnd);
            DeleteCompressedPaths(childPath, child, deleter, cleanupStart, cleanupEnd);
        }
    }

    private static void DeleteCompressedPaths<TDeleter>(TreePath path, TrieNode node, TDeleter deleter,
        in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) where TDeleter : IRangeDeleter
    {
        byte[] key = node.Key!;
        int count = node.IsExtension ? key.Length - 1 : key.Length;
        for (int i = 0; i < count; i++)
        {
            path = path.Append(key[i]);
            if (path.ToLowerBoundPath() >= cleanupStart && path.ToUpperBoundPath() <= cleanupEnd)
                deleter.DeleteNode(path);
        }
    }

    private static TrieNode Resolve(byte[] rlp, in TreePath path)
    {
        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, path);
        return node;
    }

    private static void DeleteBesideKey<TDeleter>(in TreePath path, byte[] key, TDeleter deleter, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) where TDeleter : IRangeDeleter
    {
        TreePath keyPath = path.Append(key);
        ValueHash256 subtreeStart = path.ToLowerBoundPath();
        ValueHash256 subtreeEnd = path.ToUpperBoundPath();
        ValueHash256 keyStart = keyPath.ToLowerBoundPath();
        ValueHash256 keyEnd = keyPath.ToUpperBoundPath();

        if (keyStart.CompareTo(subtreeStart) > 0)
            DeleteWithinBounds(deleter, subtreeStart, keyStart.DecrementPath(), cleanupStart, cleanupEnd);

        if (keyEnd.CompareTo(subtreeEnd) < 0)
            DeleteWithinBounds(deleter, keyEnd.IncrementPath(), subtreeEnd, cleanupStart, cleanupEnd);
    }

    private static void DeleteWithinBounds<TDeleter>(TDeleter deleter, in ValueHash256 from, in ValueHash256 to, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd)
        where TDeleter : IRangeDeleter
    {
        ValueHash256 start = from < cleanupStart ? cleanupStart : from;
        ValueHash256 end = to > cleanupEnd ? cleanupEnd : to;
        if (start <= end) deleter.DeleteRange(start, end);
    }
}
