// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Storage trie store adapter that writes trie nodes AND flat storage entries to IPersistence.IWriteBatch.
/// Uses IPersistenceReader for IsPersisted queries during snap sync.
/// </summary>
internal class PersistenceStorageTrieStoreAdapter(
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
