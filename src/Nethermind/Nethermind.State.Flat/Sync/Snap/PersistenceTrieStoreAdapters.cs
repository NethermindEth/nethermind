// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// <see cref="IScopedTrieStore"/> for the state trie that reads/writes trie nodes through an
/// <see cref="IPersistence"/> reader/write-batch pair. Writes only trie <em>nodes</em>; flat account
/// leaves are the caller's responsibility. Used by flat snap sync and BAL healing.
/// </summary>
public sealed class PersistenceTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch,
    bool enableDoubleWriteCheck) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

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
                throw new InvalidOperationException($"Double state rlp write. {path}");
            }
            writeBatch.SetStateTrieNode(path, node.FullRlp.AsSpan());
            return node;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// <see cref="IScopedTrieStore"/> for a single account's storage trie that reads/writes trie nodes
/// through an <see cref="IPersistence"/> reader/write-batch pair. Writes only trie <em>nodes</em>;
/// flat storage leaves are the caller's responsibility. Used by flat snap sync and BAL healing.
/// </summary>
public sealed class PersistenceStorageTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch,
    Hash256 addressHash,
    bool enableDoubleWriteCheck) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        reader.TryLoadStorageRlp(addressHash, path, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StorageCommitter(writeBatch, reader, addressHash, enableDoubleWriteCheck);

    private sealed class StorageCommitter(IPersistence.IWriteBatch writeBatch, IPersistence.IPersistenceReader reader, Hash256 address, bool enableDoubleWriteCheck) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            if (enableDoubleWriteCheck && reader.TryLoadStorageRlp(address, path, ReadFlags.None) != null)
            {
                throw new InvalidOperationException($"Double storage rlp write. {address} {path}");
            }
            writeBatch.SetStorageTrieNode(address, path, node.FullRlp.AsSpan());
            return node;
        }

        public void Dispose() { }
    }
}
