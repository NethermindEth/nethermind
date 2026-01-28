// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapStorageTree adapter for flat snap sync.
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree : ISnapStorageTree
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly StorageTree _tree;
    private readonly Hash256 _addressHash;

    public FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256 addressHash, ILogManager logManager)
    {
        _reader = reader;
        _writeBatch = writeBatch;
        _addressHash = addressHash;
        _tree = new StorageTree(new PersistenceStorageTrieStoreAdapter(reader, writeBatch, addressHash), logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None) is not null;

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) =>
        _tree.BulkSet(entries, flags);

    public void UpdateRootHash() => _tree.UpdateRootHash();

    public void Commit(WriteFlags writeFlags) => _tree.Commit(writeFlags: writeFlags);

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
