// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using BulkSetEntry = Nethermind.Trie.PatriciaTree.BulkSetEntry;

namespace Nethermind.Trie.NodeLog;

/// <summary>Applies entries to a trie of node records, writing every new node to the trie node log, like <see cref="PatriciaTree.BulkSet"/>.</summary>
/// <remarks>
/// A node referenced by hash is read in place from the log when its pointer resolves, and otherwise from the node source, without being
/// added to the log. Entries are bucketed by nibble one level at a time; a leaf or extension the entries split is walked in place, less
/// the key nibbles the split dropped, and copied out only when it changes. New nodes stay pending in a <see cref="BulkSetArena"/> until
/// the commit encodes them straight into the log one path depth at a time, deepest first, hashing each level together.
/// Nodes that become unreachable are not removed from the log.
/// </remarks>
internal static partial class LogBulkSet
{
    private const int InPlaceSortThreshold = 32;
    private const int TinySortMaxCount = 3;
    private const int MaxKeyNibbles = BulkSetArena.MaxKeyNibbles;
    private const int BranchChildCount = BulkSetArena.BranchChildCount;

    /// <summary>Sets every entry's key to its value, deleting it when the value is null or empty, and returns the new root.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    /// <param name="entries">Reordered in place; keys must be unique.</param>
    /// <param name="written">Receives the path and pointer of every node written.</param>
    /// <param name="canParallelize">Ignored: the bulk set runs serially.</param>
    /// <exception cref="MissingTrieNodeException">A node on a changed path is in neither the log nor the source.</exception>
    /// <exception cref="InvalidOperationException">Two entries have the same key.</exception>
    public static TrieRoot BulkSet(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root,
        in ArrayPoolListRef<BulkSetEntry> entries, WrittenNodes written, bool canParallelize)
    {
        if (entries.Count == 0) return root;

        BulkSetArena arena = BulkSetArena.Rent();
        try
        {
            Context context = new(log, source, address, arena, written);
            Source rootNode = context.LoadRoot(root);
            TreePath path = TreePath.Empty;
            Updated updated;
            if (entries.Count < InPlaceSortThreshold)
            {
                // The in-place sorts below the threshold need no separate buffer.
                updated = context.Update(entries.AsSpan(), entries.AsSpan(), ref path, rootNode);
            }
            else
            {
                using ArrayPoolListRef<BulkSetEntry> sortBuffer = new(entries.Count, entries.Count);
                updated = context.Update(entries.AsSpan(), sortBuffer.AsSpan(), ref path, rootNode);
            }

            if (!updated.Changed) return root;
            if (updated.PendingIndex < 0) return TrieRoot.Empty;

            ChildRef top = context.Commit(updated.PendingIndex, TreePath.Empty, isRoot: true);
            log.SetRoot(address, top.Data, top.Pointer);
            return new TrieRoot(top.Data, top.Pointer);
        }
        finally
        {
            Metrics.AddBulkSetNodesFromLog(arena.NodesFromLog);
            Metrics.AddBulkSetNodesFromSource(arena.NodesFromSource);
            BulkSetArena.Return(arena);
        }
    }

    /// <summary>The root of <paramref name="rootHash"/>, with its record pointer when the log indexes it.</summary>
    public static TrieRoot ResolveRoot(TrieNodeLog log, Hash256? address, in ValueHash256 rootHash) =>
        new(rootHash, rootHash == ValueKeccak.EmptyTreeHash ? 0 : log.FindRoot(address, rootHash));

    /// <summary>Loads the nodes on the path to <paramref name="key"/> that the log does not hold from <paramref name="source"/>.</summary>
    /// <remarks>Stops silently where the path ends or the source misses.</remarks>
    /// <param name="root">Its pointer is used as given, as <see cref="ResolveRoot"/> returns it.</param>
    public static void WarmUp(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root, in ValueHash256 key) =>
        Walk(log, source, address, root, key, throwOnMissing: false, out _);

    /// <param name="root">Its pointer is used as given, as <see cref="ResolveRoot"/> returns it.</param>
    /// <returns>The value at <paramref name="key"/>, or null when the trie has none.</returns>
    /// <exception cref="MissingTrieNodeException">A node on the path is in neither the log nor the source.</exception>
    public static byte[]? TryGet(TrieNodeLog log, INodeRecordSource source, Hash256? address, TrieRoot root, in ValueHash256 key)
    {
        Source leaf = Walk(log, source, address, root, key, throwOnMissing: true, out int depth);
        if (leaf.Kind == SourceKind.None) return null;

        Span<byte> keyNibbles = stackalloc byte[MaxKeyNibbles];
        Span<byte> leafKey = stackalloc byte[MaxKeyNibbles];
        Nibbles.BytesToNibbleBytes(key.Bytes, keyNibbles);
        int leafKeyLength = leaf.CopyKey(leafKey);
        return leafKey[..leafKeyLength].SequenceEqual(keyNibbles[depth..]) ? leaf.LeafValue().Span.ToArray() : null;
    }

    /// <returns>The leaf on the path to <paramref name="key"/>, at <paramref name="depth"/>, or none when the path ends before one or a node is missing.</returns>
    private static Source Walk(TrieNodeLog log, INodeRecordSource source, Hash256? address, in TrieRoot root, in ValueHash256 key, bool throwOnMissing, out int depth)
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

            if (!log.TryLocate(child.Pointer, out NodeRecordRef record) && !source.TryLoad(path, child.Data, out record))
            {
                return throwOnMissing ? ThrowMissingNode(address, path, child.Data) : default;
            }

            RecordView view = record.View;
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

    [DoesNotReturn, StackTraceHidden]
    private static Source ThrowMissingNode(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        throw new MissingTrieNodeException($"Trie node missing hash={hash} address={address} path={path}", address, path, new Hash256(hash));

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNonUniqueKeys() => throw new InvalidOperationException("Non unique entry keys");

    /// <summary>What a bulk set on one arena shares: the log and source it reads, the trie it writes and where it records writes.</summary>
    private readonly partial struct Context(TrieNodeLog log, INodeRecordSource source, Hash256? address, BulkSetArena arena, WrittenNodes written)
    {
        private readonly TrieNodeLog _log = log;
        private readonly INodeRecordSource _source = source;
        private readonly Hash256? _address = address;
        private readonly BulkSetArena _arena = arena;
        private readonly WrittenNodes _written = written;
    }
}
