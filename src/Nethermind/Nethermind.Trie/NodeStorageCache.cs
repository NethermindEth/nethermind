// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private readonly SeqlockCache<NodeKey, byte[]> _cache = new();

    private volatile bool _enabled = false;

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

    /// <summary>Disables and clears the cache.</summary>
    /// <returns><see langword="true"/> when the cache was enabled; this does not indicate whether it contained entries.</returns>
    public bool ClearCaches()
    {
        bool wasEnabled = _enabled;
        _enabled = false;
        _cache.Clear();
        return wasEnabled;
    }
}
