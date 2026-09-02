// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ArchiveProofNodeCache(int capacity)
{
    private readonly LruCache<CacheKey, byte[]> _nodes = new(capacity, nameof(ArchiveProofNodeCache));

    public bool TryGet(in ValueHash256 trieScope, in TreePath path, ulong block, out byte[]? rlp)
    {
        CacheKey key = new(trieScope, path.Path, (byte)path.Length, block);
        lock (_nodes)
        {
            return _nodes.TryGet(key, out rlp);
        }
    }

    public void Set(in ValueHash256 trieScope, in TreePath path, ulong block, byte[] rlp)
    {
        CacheKey key = new(trieScope, path.Path, (byte)path.Length, block);
        lock (_nodes)
        {
            _nodes.Set(key, rlp);
        }
    }

    private readonly record struct CacheKey(ValueHash256 TrieScope, ValueHash256 Path, byte PathLength, ulong Block);
}
