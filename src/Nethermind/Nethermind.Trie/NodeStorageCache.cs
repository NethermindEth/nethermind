// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private readonly SeqlockCache<NodeKey, byte[]> _cache = new();

    // Cross-block trie node cache. Trie nodes are content-addressed (keyed by hash),
    // so stale entries are impossible — the same hash always maps to the same RLP.
    // This cache is never cleared between blocks, only evicted naturally by the
    // set-associative replacement policy.
    // setsLog2=20 → 1M sets × 2 ways = 2M entries. At ~200 bytes avg RLP per node ≈ 400 MB of cached data.
    private readonly SeqlockCache<NodeKey, byte[]> _crossBlockCache = new(setsLog2: 20);

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
            // Main processing path (per-block cache disabled). Still check cross-block cache.
            if (_crossBlockCache.TryGetValue(in nodeKey, out byte[]? cached))
            {
                return cached;
            }

            byte[]? value = tryLoadRlp(in nodeKey);
            if (value is not null)
            {
                _crossBlockCache.Set(in nodeKey, value);
            }
            return value;
        }

        // Prewarmer path: check per-block cache first, then cross-block, then disk.
        if (_cache.TryGetValue(in nodeKey, out byte[]? perBlock))
        {
            return perBlock;
        }

        if (_crossBlockCache.TryGetValue(in nodeKey, out byte[]? crossBlock))
        {
            _cache.Set(in nodeKey, crossBlock);
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
        // Cross-block cache is NOT cleared — trie nodes are content-addressed,
        // so entries are always valid regardless of reorgs.
        return wasEnabled;
    }
}
