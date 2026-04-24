// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Trie;

public sealed class NodeStorageCache
{
    private const int ShardCount = 8;
    private const int ShardMask = ShardCount - 1;

    private readonly SeqlockCache<NodeKey, byte[]>[] _shards;

    private volatile bool _enabled = false;

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

    public byte[]? GetOrAdd(in NodeKey nodeKey, SeqlockCache<NodeKey, byte[]>.ValueFactory tryLoadRlp)
    {
        if (!_enabled)
        {
            return tryLoadRlp(in nodeKey);
        }
        int shard = (int)((uint)nodeKey.GetHashCode() >> 16) & ShardMask;
        return _shards[shard].GetOrAdd(in nodeKey, tryLoadRlp);
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
