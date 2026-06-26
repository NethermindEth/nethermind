// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Trie.Utils;

namespace Nethermind.Trie.Pruning;

public class RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address = null, INodeStorage.IWriteBatch? writeBatch = null, int disableWalBatchSize = 0) : IScopedTrieStore
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

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(nodeStorage, address, writeFlags, writeBatch, disableWalBatchSize);

    public class Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags, INodeStorage.IWriteBatch? writeBatch = null, int disableWalBatchSize = 0) : ICommitter
    {
        private readonly bool _ownsWriteBatch = writeBatch is null;
        private readonly bool _canCommitConcurrently = writeBatch is null && (writeFlags & WriteFlags.DisableWAL) != 0;
        private INodeStorage.IWriteBatch? _writeBatch =
            writeBatch is null && (writeFlags & WriteFlags.DisableWAL) != 0
                ? new SortedNodeWriteBatcher(nodeStorage, TrieWriteBatchSettings.GetDisableWalBatchSize(disableWalBatchSize))
                : writeBatch;
        private int _concurrency = Environment.ProcessorCount;

        public void Dispose()
        {
            if (_ownsWriteBatch)
            {
                _writeBatch?.Dispose();
            }
        }

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
            }

            return node;
        }

        public bool TryRequestConcurrentQuota()
        {
            if (!_canCommitConcurrently)
            {
                return false;
            }

            if (Interlocked.Decrement(ref _concurrency) >= 0)
            {
                return true;
            }

            ReturnConcurrencyQuota();
            return false;
        }

        public void ReturnConcurrencyQuota()
        {
            if (_canCommitConcurrently)
            {
                Interlocked.Increment(ref _concurrency);
            }
        }

        private INodeStorage.IWriteBatch CurrentBatch => _writeBatch ??= nodeStorage.StartWriteBatch();

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }
}
