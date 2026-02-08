// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

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

    public void Clear() => _tree.RootHash = Keccak.EmptyTreeHash;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
    }

    public void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags) => _tree.BulkSet(entries, flags);

    public void UpdateRootHash() => _tree.UpdateRootHash();

    public void Commit(WriteFlags writeFlags, ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;
        _tree.Commit(writeFlags: writeFlags);
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
