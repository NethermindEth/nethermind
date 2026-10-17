// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeLog;

/// <summary>Applies entries to a trie of node records, writing every new node to the trie node log, like <see cref="PatriciaTree.BulkSet"/>.</summary>
/// <remarks>
/// A node referenced by hash is read in place from the log when its pointer resolves, and otherwise from the node source, without being
/// added to the log. Entries are bucketed by nibble one level at a time; a leaf or extension the entries split is walked in place, less
/// the key nibbles the split dropped, and copied out only when it changes. New nodes stay pending in a <see cref="BulkSetArena"/> until
/// the commit encodes them straight into the log one path depth at a time, deepest first, hashing each level together.
/// A full branch level with enough entries may update its children in parallel, each task committing its subtree in its own arena.
/// Nodes that become unreachable are not removed from the log.
/// </remarks>
internal static partial class LogBulkSet
{
    private const int InPlaceSortThreshold = 32;
    private const int TinySortMaxCount = 3;
    private const int MaxKeyNibbles = BulkSetArena.MaxKeyNibbles;
    private const int BranchChildCount = BulkSetArena.BranchChildCount;
    private const int FullMask = (1 << BranchChildCount) - 1;
    private const int MaxWorkers = 16;

    /// <summary>Sets every entry's key to its value, deleting it when the value is null or empty, and returns the new root.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="entries">Reordered in place; keys must be unique.</param>
    /// <param name="canParallelize">Whether the children of a full branch level with enough entries may be updated in parallel.</param>
    /// <exception cref="MissingTrieNodeException">A node on a changed path is in neither the log nor the source.</exception>
    /// <exception cref="InvalidOperationException">Two entries have the same key.</exception>
    public static TrieRoot BulkSet(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root,
        in ArrayPoolListRef<BulkSetEntry> entries, bool canParallelize)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        if (entries.Count == 0) return root;

        using ParallelUnbalancedWork.WorkerScope? workers = canParallelize && !RuntimeInformation.IsSingleProcessor
            && entries.Count >= PatriciaTree.MinEntriesToParallelizeThreshold
            ? ParallelUnbalancedWork.BeginWorkerScope(Math.Min(MaxWorkers, RuntimeInformation.ProcessorCount))
            : null;
        BulkSetArena arena = BulkSetArena.Rent();
        try
        {
            Source rootNode;
            TreePath path = TreePath.Empty;
            Updated updated;
            Context context;
            if (entries.Count < InPlaceSortThreshold)
            {
                // The in-place sorts below the threshold need no separate buffer.
                BulkSetEntry[] entriesArray = entries.UnsafeGetInternalArray();
                context = new(log, source, address, arena, entriesArray, entriesArray, canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), entries.AsSpan(), flipped: false, ref path, rootNode);
            }
            else
            {
                using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
                context = new(log, source, address, arena, entries.UnsafeGetInternalArray(), sortBuffer.UnsafeGetInternalArray(), canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), sortBuffer.AsSpan(), flipped: false, ref path, rootNode);
            }

            if (!updated.Changed) return root;
            if (updated.PendingIndex < 0) return TrieRoot.Empty;

            ChildRef top = context.Commit(updated.PendingIndex, TreePath.Empty, isRoot: true);
            log.SetRoot(address, top.Data, top.Pointer);
            return new TrieRoot(top.Data, top.Pointer);
        }
        finally
        {
            ReturnArena(arena);
        }
    }

    /// <summary>The root of <paramref name="rootHash"/>, with its record pointer when the log indexes it.</summary>
    public static TrieRoot ResolveRoot(TrieNodeLog log, Hash256? address, in ValueHash256 rootHash) =>
        new(rootHash, rootHash == ValueKeccak.EmptyTreeHash ? 0 : log.FindRoot(address, rootHash));

    /// <summary>Loads the nodes on the path to <paramref name="key"/> that the log does not hold from <paramref name="source"/>.</summary>
    /// <remarks>Stops silently where the path ends or the source misses.</remarks>
    /// <param name="root">Its pointer is used as given, as <see cref="ResolveRoot"/> returns it.</param>
    /// <param name="forDelete">Also loads the sibling a two-child branch collapses into when its matching leaf is deleted.</param>
    public static void WarmUp(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root, in ValueHash256 key, bool forDelete = false)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        Walk(log, source, address, root, key, throwOnMissing: false, out _, forDelete);
    }

    /// <param name="root">Its pointer is used as given, as <see cref="ResolveRoot"/> returns it.</param>
    /// <returns>The value at <paramref name="key"/>, or null when the trie has none.</returns>
    /// <exception cref="MissingTrieNodeException">A node on the path is in neither the log nor the source.</exception>
    public static byte[]? TryGet(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root, in ValueHash256 key)
    {
        using TrieNodeLog.ReadLease lease = log.BeginRead();
        Source leaf = Walk(log, source, address, root, key, throwOnMissing: true, out int depth);
        if (leaf.Kind == SourceKind.None) return null;

        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Span<byte> leafKey = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        int leafKeyLength = leaf.CopyKey(leafKey);
        return leafKey[..leafKeyLength].SequenceEqual(keyNibbles[depth..]) ? leaf.LeafValue().Span.ToArray() : null;
    }

    /// <returns>The leaf on the path to <paramref name="key"/>, at <paramref name="depth"/>, or none when the path ends before one or a node is missing.</returns>
    private static Source Walk(TrieNodeLog log, INodeRecordSource source, Hash256? address, in TrieRoot root, in ValueHash256 key, bool throwOnMissing, out int depth, bool forDelete = false)
    {
        NodeRecordRef parentBranch = default;
        TreePath path = TreePath.Empty;
        ChildRef child = root.Hash == ValueKeccak.EmptyTreeHash ? default : ChildRef.ForHash(root.Hash, root.Pointer);
        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Span<byte> extensionKey = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        while (true)
        {
            depth = path.Length;
            switch (child.Kind)
            {
                case ChildRefKind.Empty:
                    return default;
                case ChildRefKind.Inline:
                    Source inlineLeaf = Source.ForInline(child);
                    if (forDelete) WarmUpDeleteSibling(log, source, parentBranch, path, inlineLeaf, keyNibbles);
                    return inlineLeaf;
            }

            if (!log.TryLocate(child.Pointer, out NodeRecordRef record) && !source.TryLoad(path, child.Data, out record))
            {
                return throwOnMissing ? ThrowMissingNode(address, path, child.Data) : default;
            }

            RecordView view = record.View;
            switch (view.Kind)
            {
                case NodeRecordKind.Leaf:
                    Source leaf = Source.ForRecord(record, 0);
                    if (forDelete) WarmUpDeleteSibling(log, source, parentBranch, path, leaf, keyNibbles);
                    return leaf;
                case NodeRecordKind.Extension:
                    int keyLength = view.KeyLength(0);
                    view.CopyKey(0, extensionKey);
                    if (!keyNibbles[depth..].StartsWith(extensionKey[..keyLength])) return default;

                    path.AppendMut(extensionKey[..keyLength]);
                    child = view.Child(0);
                    parentBranch = default;
                    break;
                default:
                    parentBranch = record;
                    path.AppendMut(keyNibbles[depth]);
                    child = view.Child(keyNibbles[depth]);
                    break;
            }
        }
    }

    // A single delete can collapse its immediate branch into an off-path sibling; an extension between them cannot.
    private static void WarmUpDeleteSibling(TrieNodeLog log, INodeRecordSource source, NodeRecordRef branch, TreePath leafPath, Source leaf, ReadOnlySpan<byte> key)
    {
        if (branch.IsNull) return;
        Span<byte> leafKey = stackalloc byte[MaxKeyNibbles];
        int keyLength = leaf.CopyKey(leafKey);
        if (!leafKey[..keyLength].SequenceEqual(key[leafPath.Length..])) return;

        int deletedNibble = key[leafPath.Length - 1];
        int siblingNibble = -1;
        ChildRef sibling = default;
        RecordView view = branch.View;
        for (int nibble = 0; nibble < BranchChildCount; nibble++)
        {
            if (nibble == deletedNibble) continue;
            ChildRef child = view.Child(nibble);
            if (child.Kind == ChildRefKind.Empty) continue;
            if (siblingNibble >= 0) return;
            siblingNibble = nibble;
            sibling = child;
        }

        if (sibling.Kind != ChildRefKind.Hash || log.TryLocate(sibling.Pointer, out _)) return;
        leafPath.TruncateOne();
        leafPath.AppendMut(siblingNibble);
        source.TryLoad(leafPath, sibling.Data, out _);
    }

    private static void ReturnArena(BulkSetArena arena)
    {
        Metrics.AddBulkSetNodesFromLog(arena.NodesFromLog);
        Metrics.AddBulkSetNodesFromSource(arena.NodesFromSource);
        BulkSetArena.Return(arena);
    }

    [DoesNotReturn, StackTraceHidden]
    private static Source ThrowMissingNode(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        throw new MissingTrieNodeException($"Trie node missing hash={hash} address={address} path={path}", address, path, new Hash256(hash));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonUniqueKeys() => throw new InvalidOperationException("Non unique entry keys");

    /// <summary>What a bulk set on one arena shares: the log and source it reads, the trie it writes and the arrays its entries are in.</summary>
    /// <param name="entries">The array of the entries passed in.</param>
    /// <param name="sortBuffer">The array of the sort buffer, the same as <paramref name="entries"/> when there is none.</param>
    private readonly partial struct Context(TrieNodeLog log, INodeRecordSource source, Hash256? address, BulkSetArena arena,
        BulkSetEntry[] entries, BulkSetEntry[] sortBuffer, bool canParallelize)
    {
        private readonly TrieNodeLog _log = log;
        private readonly INodeRecordSource _source = source;
        private readonly Hash256? _address = address;
        private readonly BulkSetArena _arena = arena;
        private readonly BulkSetEntry[] _entries = entries;
        private readonly BulkSetEntry[] _sortBuffer = sortBuffer;
        private readonly bool _canParallelize = canParallelize;
    }
}
