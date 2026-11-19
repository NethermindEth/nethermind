// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Base interface for snap sync tree operations used in FillBoundaryTree.
/// </summary>
public interface ISnapTree<in TEntry> : IDisposable where TEntry : ISnapEntry
{
    Hash256 RootHash { get; }

    void SetRootFromProof(TrieNode root);
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);
    void BulkSetAndUpdateRootHash(IReadOnlyList<TEntry> entries);

    /// <summary>Persists the nodes within [<paramref name="lowerBound"/>, <paramref name="upperBound"/>] that are not boundary proof nodes, and the entries up to <paramref name="upperBound"/>.</summary>
    void Commit(ValueHash256 lowerBound, ValueHash256 upperBound);

    /// <summary>
    /// Removes data that an earlier range left at paths that <paramref name="node"/>, a boundary branch or extension this
    /// commit persists, does not cover: under the empty children of a branch, beside the key of a leaf child of a branch,
    /// or beside the key of an extension, within the inclusive cleanup bounds.
    /// </summary>
    void DeleteOutsideChildren(in TreePath path, TrieNode node, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) { }

    /// <summary>Removes stale data inside the cleanup bounds after the entries reconstruct the complete root.</summary>
    void DeleteOutsideCompleteTree(in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd) { }

    /// <summary>
    /// Converts entries to <see cref="PatriciaTree.BulkSetEntry"/>, tracks metrics,
    /// then calls <see cref="PatriciaTree.BulkSet"/> and <see cref="PatriciaTree.UpdateRootHash"/>.
    /// </summary>
    static void DoBulkSetAndUpdateRootHash<T>(PatriciaTree tree, IReadOnlyList<T> entries) where T : ISnapEntry
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(entries.Count);
        long totalBytes = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            T entry = entries[i];
            byte[] rlpValue = entry.ToRlpValue();
            bulkEntries.Add(new PatriciaTree.BulkSetEntry(entry.Path, rlpValue));
            totalBytes += rlpValue.Length;
        }
        Interlocked.Add(ref Metrics.SnapStateSynced, totalBytes);

        tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        tree.UpdateRootHash(canBeParallel: true);
    }
}
