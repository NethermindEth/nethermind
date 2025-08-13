// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// Expose <see cref="IPruningTrieStore"/> interface without actually having any pruning logic.
/// <see cref="RawScopedTrieStore"/> does not have any concept of two level trie, just trie, because its for <see cref="PatriciaTree"/>.
/// Note: If you are using this, consider interacting with <see cref="TestWorldStateFactory"/> instead, or if you
/// actually don't need the whole worldstate or the two level trie, <see cref="RawScopedTrieStore"/>.
/// </summary>
/// <param name="nodeStorage"></param>
/// <param name="isReadOnly"></param>
public class TestRawTrieStore(INodeStorage nodeStorage, bool isReadOnly = false) : IPruningTrieStore, IReadOnlyTrieStore
{
    public TestRawTrieStore(IKeyValueStoreWithBatching kv) : this(new NodeStorage(kv))
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

    public void PersistCache(CancellationToken cancellationToken)
    {
    }

    public IReadOnlyTrieStore AsReadOnly() =>
        new TestRawTrieStore(nodeStorage, true);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new Exception("Unsupported operation");
        remove => throw new Exception("Unsupported operation");
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => throw new Exception("Unsupported operatioon");
    public Lock.Scope LockDirtyNodes()
    {
        return new Lock.Scope();
    }
}
