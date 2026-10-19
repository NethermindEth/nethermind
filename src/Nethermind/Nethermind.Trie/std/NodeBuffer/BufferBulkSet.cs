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
/// A node referenced by hash is read in place from the buffer when its pointer resolves, and otherwise from the node source, without being
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
    /// <exception cref="MissingTrieNodeException">A node on a changed path is in neither the buffer nor the source.</exception>
    /// <exception cref="InvalidOperationException">Two entries have the same key.</exception>
    public static TrieRoot BulkSet(NodeRecordBuffer buffer, INodeRecordSource source, Hash256? address, TrieRoot root,
        in ArrayPoolListRef<BulkSetEntry> entries, bool canParallelize)
    {
        using NodeRecordBuffer.ReadLease lease = buffer.BeginRead();
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
                context = new(buffer, source, address, arena, entriesArray, entriesArray, canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), entries.AsSpan(), flipped: false, ref path, rootNode);
            }
            else
            {
                using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
                context = new(buffer, source, address, arena, entries.UnsafeGetInternalArray(), sortBuffer.UnsafeGetInternalArray(), canParallelize);
                rootNode = context.LoadRoot(root);
                updated = context.Update(entries.AsSpan(), sortBuffer.AsSpan(), flipped: false, ref path, rootNode);
            }

            if (!updated.Changed) return new TrieRoot(root.Hash, NodeRecordCodec.PersistentPointer(root.Pointer));
            if (updated.PendingIndex < 0) return TrieRoot.Empty;

            ChildRef top = context.Commit(updated.PendingIndex, TreePath.Empty, isRoot: true);
            buffer.SetRoot(address, top.Data, top.Pointer);
            return new TrieRoot(top.Data, top.Pointer);
        }
        finally
        {
            ReturnArena(arena);
        }
    }

    /// <summary>The root of <paramref name="rootHash"/>, with its record pointer when the buffer indexes it.</summary>
    public static TrieRoot ResolveRoot(NodeRecordBuffer buffer, Hash256? address, in ValueHash256 rootHash) =>
        new(rootHash, rootHash == ValueKeccak.EmptyTreeHash ? 0 : buffer.FindRoot(address, rootHash));

    /// <param name="root">Its pointer is used as given, as <see cref="ResolveRoot"/> returns it.</param>
    /// <returns>The value at <paramref name="key"/>, or null when the trie has none.</returns>
    /// <exception cref="MissingTrieNodeException">A node on the path is in neither the buffer nor the source.</exception>
    public static byte[]? TryGet(NodeRecordBuffer buffer, INodeRecordSource source, Hash256? address, TrieRoot root, in ValueHash256 key)
    {
        using NodeRecordBuffer.ReadLease lease = buffer.BeginRead();
        Source leaf = Walk(buffer, source, address, root, key, out int depth);
        if (leaf.Kind == SourceKind.None) return null;

        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Span<byte> leafKey = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        int leafKeyLength = leaf.CopyKey(leafKey);
        return leafKey[..leafKeyLength].SequenceEqual(keyNibbles[depth..]) ? leaf.LeafValue().Span.ToArray() : null;
    }

    /// <returns>The leaf on the path to <paramref name="key"/>, at <paramref name="depth"/>, or none when the path ends before one.</returns>
    private static Source Walk(NodeRecordBuffer buffer, INodeRecordSource source, Hash256? address, in TrieRoot root, in ValueHash256 key, out int depth)
    {
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
                    return Source.ForInline(child);
            }

            if (!buffer.TryLocate(child.Pointer, out NodeRecord record) && !source.TryLoad(child.Pointer, path, child.Data, out record))
            {
                return ThrowMissingNode(address, path, child.Data);
            }

            NodeRecordRef view = record.View;
            switch (view.Kind)
            {
                case NodeRecordKind.Leaf:
                    return Source.ForRecord(record, 0);
                case NodeRecordKind.Extension:
                    int keyLength = view.KeyLength(0);
                    view.CopyKey(0, extensionKey);
                    if (!keyNibbles[depth..].StartsWith(extensionKey[..keyLength])) return default;

                    path.AppendMut(extensionKey[..keyLength]);
                    child = view.Child(0);
                    break;
                default:
                    path.AppendMut(keyNibbles[depth]);
                    child = view.Child(keyNibbles[depth]);
                    break;
            }
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

    /// <summary>What a bulk set on one arena shares: the buffer and source it reads, the trie it writes and the arrays its entries are in.</summary>
    /// <param name="entries">The array of the entries passed in.</param>
    /// <param name="sortBuffer">The array of the sort buffer, the same as <paramref name="entries"/> when there is none.</param>
    private readonly partial struct Context(NodeRecordBuffer buffer, INodeRecordSource source, Hash256? address, BulkSetArena arena,
        BulkSetEntry[] entries, BulkSetEntry[] sortBuffer, bool canParallelize)
    {
        private readonly NodeRecordBuffer _buffer = buffer;
        private readonly INodeRecordSource _source = source;
        private readonly Hash256? _address = address;
        private readonly BulkSetArena _arena = arena;
        private readonly BulkSetEntry[] _entries = entries;
        private readonly BulkSetEntry[] _sortBuffer = sortBuffer;
        private readonly bool _canParallelize = canParallelize;
    }
}
