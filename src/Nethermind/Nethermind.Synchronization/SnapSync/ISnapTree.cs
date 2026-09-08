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
    void Commit(ValueHash256 upperBound);

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
        tree.UpdateRootHash();
    }
}
