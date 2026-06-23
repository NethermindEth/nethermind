// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address = null) : IScopedTrieStore
{
    public RawScopedTrieStore(IKeyValueStoreWithBatching kv, Hash256? address = null) : this(new NodeStorage(kv), address)
    {
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        nodeStorage.Get(address, path, hash, flags)
        ?? throw new MissingTrieNodeException("Node missing", address, path, hash);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        nodeStorage.Get(address, path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => new RawScopedTrieStore(nodeStorage, address);

    public INodeStorage.KeyScheme Scheme => nodeStorage.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(nodeStorage, address, writeFlags);

    public class Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags) : ICommitter
    {
        private const int DisableWalBatchSize = 512;

        private INodeStorage.IWriteBatch? _writeBatch;
        private int _pendingWrites;

        public void Dispose() => _writeBatch?.Dispose();

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            if (!node.IsBoundaryProofNode)
            {
                if (node.Keccak is null)
                {
                    ThrowUnknownHash(node);
                }

                node.IsPersisted = true;
                CurrentBatch.Set(address, path, node.Keccak, node.FullRlp.AsSpan(), writeFlags);
                FlushDisableWalBatchIfFull();
            }

            return node;
        }

        private INodeStorage.IWriteBatch CurrentBatch => _writeBatch ??= nodeStorage.StartWriteBatch();

        private void FlushDisableWalBatchIfFull()
        {
            if ((writeFlags & WriteFlags.DisableWAL) == 0 || ++_pendingWrites < DisableWalBatchSize)
            {
                return;
            }

            _writeBatch?.Dispose();
            _writeBatch = null;
            _pendingWrites = 0;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }
}
