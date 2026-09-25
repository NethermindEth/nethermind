// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.Core.Test;

/// <summary>
/// Exposes a raw trie store backed by test storage.
/// <see cref="Nethermind.Trie.Pruning.RawScopedTrieStore"/> stores trie nodes directly without a cache.
/// </summary>
public class TestRawTrieStore(INodeStorage nodeStorage) : RawTrieStore(nodeStorage)
{
    public TestRawTrieStore(IKeyValueStoreWithBatching kv) : this(new TestNodeStorage(kv))
    {
    }
}
