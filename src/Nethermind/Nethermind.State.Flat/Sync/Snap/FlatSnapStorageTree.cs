// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (storage).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree : ISnapTree
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly StorageTree _tree;
    private readonly Hash256 _addressHash;
    private readonly SnapUpperBoundAdapter _adapter;

    public FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256 addressHash, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _addressHash = addressHash;
        _adapter = new SnapUpperBoundAdapter(new PersistenceStorageTrieStoreAdapter(reader, writeBatch, addressHash));
        _tree = new StorageTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSetAndUpdateRootHash(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries)
    {
        _tree.BulkSet(entries, PatriciaTree.Flags.WasSorted);
        _tree.UpdateRootHash();
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;
        _tree.Commit(writeFlags: WriteFlags.DisableWAL);
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }

    /// <summary>
    /// Storage trie store adapter that writes trie nodes AND flat storage entries to IPersistence.IWriteBatch.
    /// Uses IPersistenceReader for IsPersisted queries during snap sync.
    /// </summary>
    private class PersistenceStorageTrieStoreAdapter(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch writeBatch,
        Hash256 addressHash) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            reader.TryLoadStorageRlp(addressHash, path, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
            new StorageCommitter(writeBatch, addressHash);

        private sealed class StorageCommitter(IPersistence.IWriteBatch writeBatch, Hash256 address) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                writeBatch.SetStorageTrieNode(address, path, node);
                FlatEntryWriter.WriteStorageFlatEntries(writeBatch, address, path, node);
                return node;
            }

            public void Dispose() { }
        }
    }

}
