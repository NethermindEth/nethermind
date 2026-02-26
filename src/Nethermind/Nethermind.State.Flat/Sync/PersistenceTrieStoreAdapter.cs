// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Trie store adapter that writes trie nodes AND flat entries to IPersistence.IWriteBatch.
/// Uses IPersistenceReader for IsPersisted queries during snap sync.
/// </summary>
internal class PersistenceTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        reader.TryLoadStateRlp(path, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StateCommitter(writeBatch);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null ? this : new PersistenceStorageTrieStoreAdapter(reader, writeBatch, address);

    private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            writeBatch.SetStateTrieNode(path, node);
            FlatEntryWriter.WriteAccountFlatEntries(writeBatch, ref path, node);
            return node;
        }

        public void Dispose() { }
        public bool TryRequestConcurrentQuota() => false;
        public void ReturnConcurrencyQuota() { }
    }
}

/// <summary>
/// Storage trie store adapter that writes trie nodes AND flat storage entries to IPersistence.IWriteBatch.
/// Uses IPersistenceReader for IsPersisted queries during snap sync.
/// </summary>
internal class PersistenceStorageTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch,
    Hash256 addressHash) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

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
        public bool TryRequestConcurrentQuota() => false;
        public void ReturnConcurrencyQuota() { }
    }
}
