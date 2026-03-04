// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private readonly bool _enableDoubleWriteCheck;
    private SnapUpperBoundAdapter _adapter;
    private readonly StateTree _tree;

    public FlatSnapStateTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, bool enableDoubleWriteCheck, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _enableDoubleWriteCheck = enableDoubleWriteCheck;
        _adapter = new SnapUpperBoundAdapter(new PersistenceTrieStoreAdapter(reader, writeBatch, enableDoubleWriteCheck));
        _tree = new StateTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStateRlp(path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries, ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkEntries = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            PathWithAccount account = entries[i];
            Rlp rlp = account.Account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Rlp.Encode(account.Account);
            bulkEntries.Add(new PatriciaTree.BulkSetEntry(account.Path, rlp.Bytes));
            Interlocked.Add(ref Nethermind.Synchronization.Metrics.SnapStateSynced, rlp.Bytes.Length);

            if (account.Path <= upperBound)
            {
                Hash256 pathHash = account.Path.ToCommitment();
                if (_enableDoubleWriteCheck && _reader.GetAccountRaw(pathHash) is not null)
                    throw new Exception($"Double account flat write. {account.Path}");
                _writeBatch.SetAccountRaw(pathHash, account.Account);
            }
        }

        _tree.BulkSet(bulkEntries, PatriciaTree.Flags.WasSorted);
        _tree.UpdateRootHash();
    }

    public void Commit() =>
        _tree.Commit(true, WriteFlags.DisableWAL);

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }

    /// <summary>
    /// Trie store adapter that writes trie nodes AND flat entries to IPersistence.IWriteBatch.
    /// Uses IPersistenceReader for IsPersisted queries during snap sync.
    /// </summary>
    private class PersistenceTrieStoreAdapter(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch,
        bool enableDoubleWriteCheck) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStateRlp(path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StateCommitter(writeBatch, reader, enableDoubleWriteCheck);

        private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch, IPersistence.IPersistenceReader reader, bool enableDoubleWriteCheck) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                if (enableDoubleWriteCheck && reader.TryLoadStateRlp(path, ReadFlags.None) != null)
                {
                    throw new Exception($"Double state rlp write. {path}");
                }
                writeBatch.SetStateTrieNode(path, node);
                return node;
            }

            public void Dispose() { }
        }
    }

}
