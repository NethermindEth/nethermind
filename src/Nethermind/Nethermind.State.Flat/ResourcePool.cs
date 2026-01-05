// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;
using Prometheus;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State;

public class ResourcePool(IFlatDbConfig flatConfig)
{

    private class ConcurrentQueuePool<T>(int maxCapacity = 16) where T : notnull, IDisposable, IResettable
    {
        private ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();
        public double PooledItemCount => _queue.Count;

        public bool TryGet([NotNullWhen(true)] out T? item)
        {
            return _queue.TryDequeue(out item);
        }

        public bool Return(T item)
        {
            item.Reset();
            if (_queue.Count >= maxCapacity)
            {
                item.Dispose();
                return false;
            }
            _queue.Enqueue(item);
            return true;
        }

    }

    private class ConcurrentStackPool<T>(int maxCapacity = 16) where T : notnull, IDisposable, IResettable
    {
        private ConcurrentStack<T> _queue = new ConcurrentStack<T>();
        public double PooledItemCount => _queue.Count;

        public bool TryGet([NotNullWhen(true)] out T? item)
        {
            return _queue.TryPop(out item);
        }

        public bool Return(T item)
        {
            item.Reset();
            if (_queue.Count >= maxCapacity)
            {
                item.Dispose();
                return false;
            }
            _queue.Push(item);
            return true;
        }

    }

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ConcurrentQueuePool<SnapshotContent>> _snapshotPools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new ConcurrentQueuePool<SnapshotContent>(flatConfig.CompactSize + 8) },
        { IFlatDiffRepository.SnapshotBundleUsage.PostMainBlockProcessing , new ConcurrentQueuePool<SnapshotContent>(1) },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new ConcurrentQueuePool<SnapshotContent>(Environment.ProcessorCount * 4) },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new ConcurrentQueuePool<SnapshotContent>(1) },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new ConcurrentQueuePool<SnapshotContent>(4) },
    };

    private Dictionary<IFlatDiffRepository.SnapshotBundleUsage, ConcurrentStackPool<CachedResource>> _cachedResourcePools = new()
    {
        { IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing , new ConcurrentStackPool<CachedResource>(4) },
        { IFlatDiffRepository.SnapshotBundleUsage.PostMainBlockProcessing , new ConcurrentStackPool<CachedResource>(1) },
        { IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv , new ConcurrentStackPool<CachedResource>(Environment.ProcessorCount * 4) },
        { IFlatDiffRepository.SnapshotBundleUsage.StateReader , new ConcurrentStackPool<CachedResource>(1) },
        { IFlatDiffRepository.SnapshotBundleUsage.Compactor , new ConcurrentStackPool<CachedResource>(1) }
    };

    private static Counter _createdSnapshotContent = DevMetric.Factory.CreateCounter("resourcepool_created_snapshot_content", "created snapshot content", "compacted");
    private static Gauge _activeSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_active_snapshot_content", "active snapshot content", "category");
    private static Gauge _cachedSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_cached_snapshot_content", "active snapshot content", "category");
    private static Gauge _poolFullSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_pool_full_snapshot_content", "active snapshot content", "category");

    private static Counter _createdCachedResource = DevMetric.Factory.CreateCounter("resourcepool_created_cached_resource", "created snapshot content", "compacted");
    private static Gauge _activeCachedResource = DevMetric.Factory.CreateGauge("resourcepool_active_cached_resource", "active snapshot content", "category");
    private static Gauge _cachedCachedResource = DevMetric.Factory.CreateGauge("resourcepool_cached_cached_resource", "active snapshot content", "category");
    private static Gauge _poolFullCachedResource = DevMetric.Factory.CreateGauge("resourcepool_pool_full_cached_resource", "active snapshot content", "category");

    public SnapshotContent GetSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        _activeSnapshotContent.WithLabels(usage.ToString()).Inc();
        if (_snapshotPools[usage].TryGet(out var snapshotContent))
        {
            _cachedSnapshotContent.WithLabels(usage.ToString()).Set(_snapshotPools[usage].PooledItemCount);
            return snapshotContent;
        }

        _createdSnapshotContent.WithLabels(usage.ToString()).Inc();
        return new SnapshotContent(
            Accounts: new ConcurrentDictionary<AddressAsKey, Account?>(),
            Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?>(),
            SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
            StateNodes: new ConcurrentDictionary<TreePath, TrieNode>(),
            StorageNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
        );
    }

    public void ReturnSnapshotContent(IFlatDiffRepository.SnapshotBundleUsage usage, SnapshotContent snapshotContent)
    {
        _activeSnapshotContent.WithLabels(usage.ToString()).Dec();
        if (!_snapshotPools[usage].Return(snapshotContent))
        {
            _poolFullSnapshotContent.WithLabels(usage.ToString()).Inc();
        }

        _cachedSnapshotContent.WithLabels(usage.ToString()).Set(_snapshotPools[usage].PooledItemCount);
    }

    private CachedResource.Size _lastCachedResourceSize = new CachedResource.Size(1024, 1024);

    public CachedResource GetCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        var queue = _cachedResourcePools[usage];

        _activeCachedResource.WithLabels(usage.ToString()).Inc();
        if (queue.TryGet(out var cachedResource))
        {
            _cachedCachedResource.WithLabels(usage.ToString()).Set(queue.PooledItemCount);
            return cachedResource;
        }

        _createdCachedResource.WithLabels(usage.ToString()).Inc();
        return new CachedResource(_lastCachedResourceSize);
    }

    public void ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage usage, CachedResource cachedResource)
    {
        _activeCachedResource.WithLabels(usage.ToString()).Dec();
        var queue = _cachedResourcePools[usage];
        if (!queue.Return(cachedResource))
        {
            _lastCachedResourceSize = cachedResource.GetSize();
            _poolFullCachedResource.WithLabels(usage.ToString()).Inc();
        }

        _cachedCachedResource.WithLabels(usage.ToString()).Set(queue.PooledItemCount);
    }

    public Snapshot CreateSnapshot(StateId from, StateId to, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        return new Snapshot(
            from,
            to,
            content: GetSnapshotContent(usage),
            resourcePool: this,
            usage: usage);
    }
}
