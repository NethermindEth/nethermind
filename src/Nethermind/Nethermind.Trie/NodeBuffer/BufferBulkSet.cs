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

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Applies entries to a trie of node records, writing every new node to the trie node buffer, like <see cref="PatriciaTree.BulkSet"/>.</summary>
/// <remarks>
/// A node referenced by hash is read in place from the store's buffer when its pointer resolves, and otherwise loaded by the store, without being
/// added to the buffer. Entries are bucketed by nibble one level at a time; a leaf or extension the entries split is walked in place, less
/// the key nibbles the split dropped, and copied out only when it changes. New nodes stay pending in a <see cref="BulkSetArena"/> until
/// the commit encodes them straight into the buffer one path depth at a time, deepest first, hashing each level together.
/// A full branch level with enough entries may update its children in parallel, each task committing its subtree in its own arena.
/// Nodes that become unreachable are not removed from the buffer.
/// </remarks>
internal static partial class BufferBulkSet
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
    /// <exception cref="MissingTrieNodeException">The store has no node on a changed path.</exception>
    /// <exception cref="InvalidOperationException">Two entries have the same key.</exception>
    public static TrieRoot BulkSet(INodeRecordStore store, Hash256? address, TrieRoot root,
        in ArrayPoolListRef<BulkSetEntry> entries, bool canParallelize)
    {
        if (entries.Count == 0) return new TrieRoot(root.Hash, NodeRecordCodec.PersistentPointer(root.Pointer));

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
                context = new(store, address, arena, entriesArray, entriesArray, canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), entries.AsSpan(), flipped: false, ref path, rootNode);
            }
            else
            {
                using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
                context = new(store, address, arena, entries.UnsafeGetInternalArray(), sortBuffer.UnsafeGetInternalArray(), canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), sortBuffer.AsSpan(), flipped: false, ref path, rootNode);
            }

            if (!updated.Changed) return new TrieRoot(root.Hash, NodeRecordCodec.PersistentPointer(root.Pointer));
            if (updated.PendingIndex < 0) return TrieRoot.Empty;

            ChildRef top = context.Commit(updated.PendingIndex, TreePath.Empty, isRoot: true);
            return new TrieRoot(top.Data, top.Pointer);
        }
        finally
        {
            ReturnArena(arena);
        }
    }

    private static void ReturnArena(BulkSetArena arena)
    {
        Metrics.AddBulkSetNodesFromBuffer(arena.NodesFromBuffer);
        Metrics.AddBulkSetNodesFromSource(arena.NodesFromSource);
        BulkSetArena.Return(arena);
    }

    [DoesNotReturn, StackTraceHidden]
    private static Source ThrowMissingNode(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        throw new MissingTrieNodeException($"Trie node missing hash={hash} address={address} path={path}", address, path, new Hash256(hash));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonUniqueKeys() => throw new InvalidOperationException("Non unique entry keys");

    /// <summary>What a bulk set on one arena shares: the store it reads and writes, the trie it writes and the arrays its entries are in.</summary>
    /// <param name="entries">The array of the entries passed in.</param>
    /// <param name="sortBuffer">The array of the sort buffer, the same as <paramref name="entries"/> when there is none.</param>
    private readonly partial struct Context(INodeRecordStore store, Hash256? address, BulkSetArena arena,
        BulkSetEntry[] entries, BulkSetEntry[] sortBuffer, bool canParallelize)
    {
        private readonly INodeRecordStore _store = store;
        private readonly Hash256? _address = address;
        private readonly BulkSetArena _arena = arena;
        private readonly BulkSetEntry[] _entries = entries;
        private readonly BulkSetEntry[] _sortBuffer = sortBuffer;
        private readonly bool _canParallelize = canParallelize;
    }
}
