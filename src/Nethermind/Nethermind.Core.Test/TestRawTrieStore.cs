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
public class TestRawTrieStore(INodeStorage nodeStorage, bool isReadOnly = false) : RawTrieStore(nodeStorage), IPruningTrieStore
{
    public TestRawTrieStore(IKeyValueStoreWithBatching kv) : this(new NodeStorage(kv))
    {
    }

    private readonly INodeStorage _nodeStorage = nodeStorage;

    public void PersistCache(CancellationToken cancellationToken)
    {
    }

    public override ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        if (isReadOnly) return NullCommitter.Instance;
        return new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);
    }

    public IReadOnlyTrieStore AsReadOnly() =>
        new TestRawTrieStore(_nodeStorage, true);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new Exception("Unsupported operation");
        remove => throw new Exception("Unsupported operation");
    }

    public IReadOnlyKeyValueStore TrieNodeRlpStore => throw new Exception("Unsupported operatioon");

    private Lock _scopeLock = new Lock();
    private Lock _pruneLock = new Lock();

    public TrieStore.StableLockScope PrepareStableState(CancellationToken cancellationToken)
    {
        var scopeLockScope = _scopeLock.EnterScope();
        var pruneLockScope = _pruneLock.EnterScope();

        return new TrieStore.StableLockScope
        {
            scopeLockScope = scopeLockScope,
            pruneLockScope = pruneLockScope,
        };
    }
}
