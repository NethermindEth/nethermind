// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private const int ShardCount = 8;
    private const int ShardMask = ShardCount - 1;

    private readonly SeqlockCache<NodeKey, byte[]>[] _shards;

    private volatile bool _enabled = false;

    private long _hits;
    private long _misses;

    public NodeStorageCache()
    {
        _shards = new SeqlockCache<NodeKey, byte[]>[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            _shards[i] = new SeqlockCache<NodeKey, byte[]>();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public long Hits => Volatile.Read(ref _hits);
    public long Misses => Volatile.Read(ref _misses);

    public void ResetCounters()
    {
        Volatile.Write(ref _hits, 0);
        Volatile.Write(ref _misses, 0);
    }

    public byte[]? GetOrAdd(in NodeKey nodeKey, SeqlockCache<NodeKey, byte[]>.ValueFactory tryLoadRlp)
    {
        if (!_enabled)
        {
            return tryLoadRlp(in nodeKey);
        }
        int shard = (int)((uint)nodeKey.GetHashCode() >> 16) & ShardMask;
        SeqlockCache<NodeKey, byte[]> cache = _shards[shard];
        if (cache.TryGetValue(in nodeKey, out byte[]? value))
        {
            Interlocked.Increment(ref _hits);
            return value;
        }
        Interlocked.Increment(ref _misses);
        value = tryLoadRlp(in nodeKey);
        cache.Set(in nodeKey, value);
        return value;
    }

    public bool ClearCaches()
    {
        bool wasEnabled = _enabled;
        _enabled = false;
        for (int i = 0; i < ShardCount; i++)
        {
            _shards[i].Clear();
        }
        return wasEnabled;
    }
}
