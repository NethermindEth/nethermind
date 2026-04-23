// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private readonly SeqlockCache<NodeKey, byte[]> _cache = new();
    private readonly SeqlockCache<NodeKey, byte[], LargeCacheSets> _crossBlockCache = new();

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
            if (_crossBlockCache.TryGetValue(in nodeKey, out byte[]? cached))
            {
                Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
                return cached;
            }

            byte[]? value = tryLoadRlp(in nodeKey);
            if (value is not null)
            {
                _crossBlockCache.Set(in nodeKey, value);
            }

            return value;
        }

        if (_cache.TryGetValue(in nodeKey, out byte[]? perBlock))
        {
            return perBlock;
        }

        if (_crossBlockCache.TryGetValue(in nodeKey, out byte[]? crossBlock))
        {
            _cache.Set(in nodeKey, crossBlock);
            Pruning.Metrics.LoadedFromRlpCacheNodesCount++;
            return crossBlock;
        }

        byte[]? loaded = tryLoadRlp(in nodeKey);
        if (loaded is not null)
        {
            _cache.Set(in nodeKey, loaded);
            _crossBlockCache.Set(in nodeKey, loaded);
        }

        return loaded;
    }

    public bool ClearCaches()
    {
        bool wasEnabled = _enabled;
        _enabled = false;
        _cache.Clear();
        return wasEnabled;
    }
}
