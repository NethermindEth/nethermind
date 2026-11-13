// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Base interface for snap sync tree operations used in FillBoundaryTree.
/// </summary>
/// <typeparam name="TEntries">The list of range entries the tree bulk-sets.</typeparam>
public interface ISnapTree<in TEntries> : IDisposable
{
    Hash256 RootHash { get; }

    void SetRootFromProof(TrieNode root);
    bool IsPersisted(in TreePath path, in ValueHash256 keccak);
    void BulkSetAndUpdateRootHash(TEntries entries);
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

        BulkSetAndUpdateRootHash(tree, bulkEntries, totalBytes);
    }

    /// <inheritdoc cref="DoBulkSetAndUpdateRootHash{T}"/>
    /// <remarks>Each slim account is re-encoded in the full format a state trie leaf holds.</remarks>
    static void DoBulkSetAndUpdateRootHash(PatriciaTree tree, IPathWithAccountList accounts)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(accounts.Count);
        long totalBytes = 0;
        for (int i = 0; i < accounts.Count; i++)
        {
            RefPathWithAccount entry = accounts[i];
            AccountStruct account = entry.Account.ToStruct();
            byte[] rlpValue = account.IsTotallyEmpty ? StateTree.EmptyAccountRlp.Bytes : AccountDecoder.Instance.EncodeAsBytes(account);
            bulkEntries.Add(new PatriciaTree.BulkSetEntry(entry.Path, rlpValue));
            totalBytes += rlpValue.Length;
        }

        BulkSetAndUpdateRootHash(tree, bulkEntries, totalBytes);
    }

    private static void BulkSetAndUpdateRootHash(PatriciaTree tree, in ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries, long totalBytes)
    {
        Interlocked.Add(ref Metrics.SnapStateSynced, totalBytes);

        tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        tree.UpdateRootHash(canBeParallel: true);
    }
}
