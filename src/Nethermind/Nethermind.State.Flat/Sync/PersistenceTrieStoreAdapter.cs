// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Trie store adapter that writes trie nodes AND flat entries to IPersistence.IWriteBatch.
/// Used by snap sync to import state directly into the flat database.
/// </summary>
internal class PersistenceTrieStoreAdapter(IPersistence.IWriteBatch writeBatch) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        null; // Snap sync builds new state, doesn't read existing

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StateCommitter(writeBatch);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null ? this : new PersistenceStorageTrieStoreAdapter(writeBatch, address);

    public IScopedTrieStore GetStorageTrieStore(Hash256 address) =>
        new PersistenceStorageTrieStoreAdapter(writeBatch, address);

    private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch) : ICommitter
    {
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            writeBatch.SetStateTrieNode(path, node);

            // For leaf nodes, also set the flat account entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;
                Account account = _accountDecoder.Decode(node.Value.Span)!;
                writeBatch.SetAccountRaw(fullPath.ToCommitment(), account);
            }

            return node;
        }

        public void Dispose() { }
        public bool TryRequestConcurrentQuota() => false;
        public void ReturnConcurrencyQuota() { }
    }
}

/// <summary>
/// Storage trie store adapter that writes trie nodes AND flat storage entries to IPersistence.IWriteBatch.
/// </summary>
internal class PersistenceStorageTrieStoreAdapter(IPersistence.IWriteBatch writeBatch, Hash256 addressHash) : AbstractMinimalTrieStore
{
    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        null; // Snap sync builds new state, doesn't read existing

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StorageCommitter(writeBatch, addressHash);

    private sealed class StorageCommitter(IPersistence.IWriteBatch writeBatch, Hash256 address) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            writeBatch.SetStorageTrieNode(address, path, node);

            // For leaf nodes, also set the flat storage entry
            if (node.IsLeaf)
            {
                ValueHash256 fullPath = path.Append(node.Key).Path;

                ReadOnlySpan<byte> value = node.Value.Span;
                byte[] toWrite;

                if (value.IsEmpty)
                {
                    toWrite = State.StorageTree.ZeroBytes;
                }
                else
                {
                    Rlp.ValueDecoderContext rlp = value.AsRlpValueContext();
                    toWrite = rlp.DecodeByteArray();
                }

                writeBatch.SetStorageRaw(address, fullPath.ToCommitment(), SlotValue.FromSpanWithoutLeadingZero(toWrite));
            }

            return node;
        }

        public void Dispose() { }
        public bool TryRequestConcurrentQuota() => false;
        public void ReturnConcurrencyQuota() { }
    }
}
