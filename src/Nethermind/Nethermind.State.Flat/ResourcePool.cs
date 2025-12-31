// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State;

public class ResourcePool
{

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ObjectPool<SnapshotContent>> _snapshotPools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing)) },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv)) },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.StateReader)) },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage.Compactor)) },
    };

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ConcurrentQueue<CachedResource>> _cachedResourcePools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new ConcurrentQueue<CachedResource>() },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new ConcurrentQueue<CachedResource>() },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new ConcurrentQueue<CachedResource>() },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new ConcurrentQueue<CachedResource>() }
    };

    private static Counter _createdSnapshotContent = DevMetric.Factory.CreateCounter("resourcepool_created_snapshot_content", "created snapshot content", "compacted");
    private static Gauge _activeSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_active_snapshot_content", "active snapshot content", "category");

    private class SnapshotContentPolicy(IFlatDiffRepository.SnapshotBundleUsage usage) : IPooledObjectPolicy<SnapshotContent>
    {
        public SnapshotContent Create()
        {
            _createdSnapshotContent.WithLabels(usage.ToString()).Inc();
            return new SnapshotContent(
                Accounts: new ConcurrentDictionary<AddressAsKey, Account?>(),
                Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?>(),
                SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
                StateNodes: new ConcurrentDictionary<TreePath, TrieNode>(),
                StorageNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
            );
        }

        public bool Return(SnapshotContent obj)
        {
            obj.Reset();
            return true;
        }
    }

    public SnapshotContent GetSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        _activeSnapshotContent.WithLabels(usage.ToString()).Inc();
        return _snapshotPools[usage].Get();
    }

    public void ReturnSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage, SnapshotContent snapshotContent)
    {
        _activeSnapshotContent.WithLabels(usage.ToString()).Dec();
        _snapshotPools[usage].Return(snapshotContent);
    }

    public ObjectPool<SnapshotContent> GetSnapshotPool(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return _snapshotPools[usage];
    }

    public CachedResource GetCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        var queue = _cachedResourcePools[usage];

        if (queue.TryDequeue(out var cachedResource))
        {
            return cachedResource;
        }

        return new CachedResource();
    }

    public void ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage, CachedResource cachedResource)
    {
        var queue = _cachedResourcePools[usage];

        if (queue.Count > MaxCachedResourceQueueSize)
        {
            cachedResource.Dispose();
        }
        else
        {
            cachedResource.Clear();
            queue.Enqueue(cachedResource);
        }
    }

    private const int MaxCachedResourceQueueSize = 16;

    public Snapshot CreateSnapshot(StateId from, StateId to, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return new Snapshot(
            from,
            to,
            content: GetSnapshotContent(usage),
            pool: GetSnapshotPool(usage: usage));
    }
}
