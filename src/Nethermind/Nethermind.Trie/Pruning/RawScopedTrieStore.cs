// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address = null) : IScopedTrieStore
{
    public RawScopedTrieStore(IKeyValueStoreWithBatching kv, Hash256? address = null) : this(new NodeStorage(kv), address)
    {
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? ret = nodeStorage.Get(address, path, hash, flags);
        if (ret is null) throw new MissingTrieNodeException("Node missing", address, path, hash);
        return ret;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => nodeStorage.Get(address, path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => new RawScopedTrieStore(nodeStorage, address);

    public INodeStorage.KeyScheme Scheme => nodeStorage.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(nodeStorage, address, writeFlags);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => nodeStorage.KeyExists(address, path, keccak);

    public class Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags) : ICommitter
    {
        INodeStorage.IWriteBatch _writeBatch = nodeStorage.StartWriteBatch();

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo)
        {
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node;

                if (node.Keccak is null)
                {
                    ThrowUnknownHash(node);
                }

                TrieNode currentNode = nodeCommitInfo.Node;
                currentNode.IsPersisted = true;
                _writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp, writeFlags);
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }
}

public class RawTrieStore(INodeStorage nodeStorage, bool isReadOnly = false) : IPruningTrieStore, IReadOnlyTrieStore
{
    public RawTrieStore(IKeyValueStoreWithBatching kv) : this(new NodeStorage(kv))
    {
    }

    void IDisposable.Dispose()
    {
    }

    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        if (isReadOnly) return NullCommitter.Instance;
        return new RawScopedTrieStore.Committer(nodeStorage, address, writeFlags);
    }

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        return new TrieNode(NodeType.Unknown, hash);
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        byte[]? ret = nodeStorage.Get(address, path, hash, flags);
        if (ret is null) throw new MissingTrieNodeException("Node missing", address, path, hash);
        return ret;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return nodeStorage.Get(address, path, hash, flags);
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        return nodeStorage.KeyExists(address, path, keccak);
    }

    public INodeStorage.KeyScheme Scheme { get; } = nodeStorage.Scheme;

    public bool HasRoot(Hash256 stateRoot)
    {
        return nodeStorage.KeyExists(null, TreePath.Empty, stateRoot);
    }

    public IScopedTrieStore GetTrieStore(Hash256? address)
    {
        return new RawScopedTrieStore(nodeStorage, address);
    }

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        return NullCommitter.Instance;
    }

    public void PersistCache(CancellationToken cancellationToken)
    {
    }

    public IReadOnlyTrieStore AsReadOnly(INodeStorage? store = null) =>
        new RawTrieStore(nodeStorage, true);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new Exception("Unsupported operation");
        remove => throw new Exception("Unsupported operation");
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore { get; }
}
