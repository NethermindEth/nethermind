// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    // 512K entries (setsLog2=18) — trie nodes are content-addressed and immutable,
    // so a large persistent cache across blocks avoids repeated trie reads.
    // Heavy blocks need millions of trie node reads; 128K evicts too aggressively.
    private readonly SeqlockCache<NodeKey, byte[]> _cache = new(18);

    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public byte[]? GetOrAdd(in NodeKey nodeKey, SeqlockCache<NodeKey, byte[]>.ValueFactory tryLoadRlp)
    {
        if (!_enabled)
        {
            return tryLoadRlp(in nodeKey);
        }
        return _cache.GetOrAdd(in nodeKey, tryLoadRlp);
    }

    public bool ClearCaches()
    {
        _cache.Clear();
        return true;
    }
}
