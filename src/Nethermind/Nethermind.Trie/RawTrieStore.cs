// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Expose <see cref="ITrieStore"/> interface directly backed by <see cref="INodeStorage"/> without any pruning
/// or buffering.
/// </summary>
/// <param name="nodeStorage"></param>
public class RawTrieStore(INodeStorage nodeStorage) : IReadOnlyTrieStore
{
    public RawTrieStore(IKeyValueStoreWithBatching kv) : this(new NodeStorage(kv))
    {
    }

    void IDisposable.Dispose()
    {
    }

    public virtual ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
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

    public IDisposable BeginScope(BlockHeader? baseBlock)
    {
        return new Reactive.AnonymousDisposable(() => { });
    }

    public IScopedTrieStore GetTrieStore(Hash256? address)
    {
        return new RawScopedTrieStore(nodeStorage, address);
    }

    public IBlockCommitter BeginBlockCommit(long blockNumber)
    {
        return NullCommitter.Instance;
    }
}
