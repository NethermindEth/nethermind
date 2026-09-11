// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

/// <summary>
/// Exposes a raw trie store backed by test storage.
/// <see cref="RawScopedTrieStore"/> stores trie nodes directly without a pruning cache.
/// </summary>
/// <param name="nodeStorage"></param>
/// <param name="isReadOnly"></param>
public class TestRawTrieStore(INodeStorage nodeStorage, bool isReadOnly = false) : RawTrieStore(nodeStorage)
{
    public TestRawTrieStore(IKeyValueStoreWithBatching kv) : this(new TestNodeStorage(kv))
    {
    }

    private readonly INodeStorage _nodeStorage = nodeStorage;

    public override ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags)
    {
        if (isReadOnly) return NullCommitter.Instance;
        return new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);
    }

    public IReadOnlyTrieStore AsReadOnly() =>
        new TestRawTrieStore(_nodeStorage, true);
}
