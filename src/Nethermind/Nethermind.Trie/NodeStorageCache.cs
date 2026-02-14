// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    // 128K entries (setsLog2=16) — trie nodes are content-addressed and immutable,
    // so a larger persistent cache across blocks avoids repeated trie reads.
    private readonly SeqlockCache<NodeKey, byte[]> _cache = new(16);

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
