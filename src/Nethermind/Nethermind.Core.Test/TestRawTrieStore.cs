// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.Core.Test;

/// <summary>
/// Exposes a raw trie store backed by test storage.
/// <see cref="Nethermind.Trie.Pruning.RawScopedTrieStore"/> does not have any concept of two level trie, just trie, because its for <see cref="PatriciaTree"/>.
/// Note: If you are using this, consider interacting with <see cref="TestWorldStateFactory"/> instead, or if you
/// actually don't need the whole worldstate or the two level trie, <see cref="Nethermind.Trie.Pruning.RawScopedTrieStore"/>.
/// </summary>
public class TestRawTrieStore(INodeStorage nodeStorage) : RawTrieStore(nodeStorage)
{
    public TestRawTrieStore(IKeyValueStoreWithBatching kv) : this(new TestNodeStorage(kv))
    {
    }
}
