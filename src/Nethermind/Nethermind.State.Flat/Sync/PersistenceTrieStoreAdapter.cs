// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
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
/// Uses IPersistenceReader for IsPersisted queries during snap sync.
/// </summary>
internal class PersistenceTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch) : AbstractMinimalTrieStore
{
    private readonly ConcurrentDictionary<ValueHash256, byte> _writtenNodes = new();

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        reader.TryLoadStateRlp(path, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StateCommitter(writeBatch, _writtenNodes);

    public override ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
        address is null ? this : new PersistenceStorageTrieStoreAdapter(reader, writeBatch, address);

    public override bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        _writtenNodes.ContainsKey(keccak) || reader.TryLoadStateRlp(path, ReadFlags.None) is not null;

    private sealed class StateCommitter(IPersistence.IWriteBatch writeBatch, ConcurrentDictionary<ValueHash256, byte> writtenNodes) : ICommitter
    {
        private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            writeBatch.SetStateTrieNode(path, node);

            // Track written node
            if (node.Keccak is not null)
            {
                writtenNodes.TryAdd(node.Keccak.ValueHash256, 0);
            }

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
/// Uses IPersistenceReader for IsPersisted queries during snap sync.
/// </summary>
internal class PersistenceStorageTrieStoreAdapter(
    IPersistence.IPersistenceReader reader,
    IPersistence.IWriteBatch writeBatch,
    Hash256 addressHash) : AbstractMinimalTrieStore
{
    private readonly ConcurrentDictionary<ValueHash256, byte> _writtenNodes = new();

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
        new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        reader.TryLoadStorageRlp(addressHash, path, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        new StorageCommitter(writeBatch, addressHash, _writtenNodes);

    public override bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        _writtenNodes.ContainsKey(keccak) || reader.TryLoadStorageRlp(addressHash, path, ReadFlags.None) is not null;

    private sealed class StorageCommitter(
        IPersistence.IWriteBatch writeBatch,
        Hash256 address,
        ConcurrentDictionary<ValueHash256, byte> writtenNodes) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            writeBatch.SetStorageTrieNode(address, path, node);

            // Track written node
            if (node.Keccak is not null)
            {
                writtenNodes.TryAdd(node.Keccak.ValueHash256, 0);
            }

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
