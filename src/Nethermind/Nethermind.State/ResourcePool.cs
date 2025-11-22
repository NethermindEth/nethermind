// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State;

public class ResourcePool
{
    private ObjectPool<SnapshotContent> _snapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(true));
    private ObjectPool<SnapshotContent> _compactedSnapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(true));
    private ObjectPool<CachedResource> _cachedResourcePool = new DefaultObjectPool<CachedResource>(new CachedResourcePolicy());

    public ObjectPool<SnapshotContent> SnapshotPool => _snapshotPool;
    public ObjectPool<SnapshotContent> CompactedSnapshotPool => _compactedSnapshotPool;

    private static Counter _createdSnapshotContent = Metrics.CreateCounter("resourcepool_created_snapshot_content", "created snapshot content");

    private class SnapshotContentPolicy(bool allow) : IPooledObjectPolicy<SnapshotContent>
    {
        public SnapshotContent Create()
        {
            _createdSnapshotContent.Inc();
            return new SnapshotContent(
                Accounts: new ConcurrentDictionary<AddressAsKey, Account?>(),
                Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), byte[]?>(),
                SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
                StateNodes: new ConcurrentDictionary<TreePath, TrieNode>(),
                StorageNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
            );
        }

        public bool Return(SnapshotContent obj)
        {
            obj.Reset();
            return allow;
        }
    }

    private class CachedResourcePolicy() : IPooledObjectPolicy<CachedResource>
    {
        public CachedResource Create()
        {
            return new CachedResource(
                new ConcurrentDictionary<TreePath, TrieNode>(),
                new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>(),
                new ConcurrentDictionary<(AddressAsKey, UInt256?), bool>()
            );
        }

        public bool Return(CachedResource obj)
        {
            obj.Clear();
            return true;
        }
    }

    public SnapshotContent GetSnapshotContent()
    {
        return _snapshotPool.Get();
    }

    public void ReturnSnapshotContent(SnapshotContent snapshotContent)
    {
        _snapshotPool.Return(snapshotContent);
    }

    public SnapshotContent GetCompactedSnapshotPool()
    {
        return _compactedSnapshotPool.Get();
    }

    public CachedResource GetCachedResource()
    {
        return _cachedResourcePool.Get();
    }

    public void ReturnCachedResource(CachedResource cachedResource)
    {
        _cachedResourcePool.Return(cachedResource);
    }
}
