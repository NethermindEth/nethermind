// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Snap;
using Nethermind.Synchronization;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (state).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStateTree : ISnapTree<PathWithAccount>
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private SnapUpperBoundAdapter _adapter;
    private readonly StateTree _tree;

    public FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _adapter = new SnapUpperBoundAdapter(new PersistenceTrieStoreAdapter(reader, writeBatch));
        _tree = new StateTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStateRlp(path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries)
    {
        // Write flat entries directly — no need to decode from trie nodes later
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            PathWithAccount account = entries[i];
            Account accountValue = account.Account;
            _writeBatch.SetAccountRaw(account.Path.ToCommitment(), accountValue);

            Rlp rlp = accountValue.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(accountValue);
            bulkEntries.Add(new PatriciaTree.BulkSetEntry(account.Path, rlp.Bytes));
            Interlocked.Add(ref Nethermind.Synchronization.Metrics.SnapStateSynced, rlp.Bytes.Length);
        }

        _tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        _tree.UpdateRootHash();
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;
        _tree.Commit(true, WriteFlags.DisableWAL);
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }

    /// <summary>
    /// Trie store adapter that writes trie nodes to IPersistence.IWriteBatch.
    /// Flat entries are written directly in BulkSetAndUpdateRootHash, so the committer only writes trie nodes.
    /// Uses IPersistenceReader for IsPersisted queries during snap sync.
    /// </summary>
    private class PersistenceTrieStoreAdapter(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StateCommitter(writeBatch);

        private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                writeBatch.SetStateTrieNode(path, node);
                return node;
            }

            public void Dispose() { }
        }
    }

}
