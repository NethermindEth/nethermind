// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Db;
using Prometheus;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// A pool of objects used to manage different sized pool objects for different category.
/// </summary>
/// <param name="flatConfig"></param>
public class ResourcePool(IFlatDbConfig flatConfig)
{
    private Dictionary<Usage, ResourcePoolCategory> _categories = new()
    {
        // For main BlockProcessing once a compacted snapshot is persisted, all `flatConfig.CompactSize` snapshot content will be returned.
        { Usage.MainBlockProcessing, new ResourcePoolCategory(Usage.MainBlockProcessing, flatConfig.CompactSize + 8, 2) },

        // PostMainBlockProcessing is a special usage right after commit of `MainBlockProcessing` which only commit once and never modified.
        { Usage.PostMainBlockProcessing, new ResourcePoolCategory(Usage.PostMainBlockProcessing, 1, 1) },

        // Note: prewarmer use readonly processing env
        // Note: readonly here means its never committed to the flat repo, but within the worldscope itself it may be committed.
        { Usage.ReadOnlyProcessingEnv, new ResourcePoolCategory(Usage.ReadOnlyProcessingEnv, Environment.ProcessorCount * 4, Environment.ProcessorCount * 4) },

        // Compacter is the large compacted snapshot. The pool usage is unclear during forward sync as the persistence
        // may lag behind block proccessing and vice versa.
        { Usage.Compactor, new ResourcePoolCategory(Usage.Compactor, 4, 1) },
        { Usage.MidCompactor, new ResourcePoolCategory(Usage.MidCompactor, 2, 1) },
    };

    private static Counter _createdSnapshotContent = DevMetric.Factory.CreateCounter("resourcepool_created_snapshot_content", "created snapshot content", "compacted");
    private static Gauge _activeSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_active_snapshot_content", "active snapshot content", "category");
    private static Gauge _cachedSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_cached_snapshot_content", "active snapshot content", "category");
    private static Gauge _poolFullSnapshotContent = DevMetric.Factory.CreateGauge("resourcepool_pool_full_snapshot_content", "active snapshot content", "category");

    private static Counter _createdCachedResource = DevMetric.Factory.CreateCounter("resourcepool_created_cached_resource", "created snapshot content", "compacted");
    private static Gauge _activeCachedResource = DevMetric.Factory.CreateGauge("resourcepool_active_cached_resource", "active snapshot content", "category");
    private static Gauge _cachedCachedResource = DevMetric.Factory.CreateGauge("resourcepool_cached_cached_resource", "active snapshot content", "category");
    private static Gauge _poolFullCachedResource = DevMetric.Factory.CreateGauge("resourcepool_pool_full_cached_resource", "active snapshot content", "category");

    public SnapshotContent GetSnapshotContent(Usage usage) => _categories[usage].GetSnapshotContent();

    public void ReturnSnapshotContent(Usage usage, SnapshotContent snapshotContent) => _categories[usage].ReturnSnapshotContent(snapshotContent);

    public TransientResource GetCachedResource(Usage usage) => _categories[usage].GetCachedResource();

    public void ReturnCachedResource(Usage usage, TransientResource transientResource) => _categories[usage].ReturnCachedResource(transientResource);

    public Snapshot CreateSnapshot(StateId from, StateId to, Usage usage)
    {
        return new Snapshot(
            from,
            to,
            content: GetSnapshotContent(usage),
            resourcePool: this,
            usage: usage);
    }

    public enum Usage
    {
        MainBlockProcessing,
        PostMainBlockProcessing,
        StateReader,
        ReadOnlyProcessingEnv,
        MidCompactor,
        Compactor,
    }

    // Using stack for better cpu cache effectiveness
    private class ConcurrentStackPool<T>(int maxCapacity = 16) where T : notnull, IDisposable, IResettable
    {
        private ConcurrentStack<T> _queue = new();
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

    private class ResourcePoolCategory(Usage usage, int snapshotContentPoolSize, int cachedResourcePoolSize)
    {
        private ConcurrentStackPool<SnapshotContent> _snapshotPool = new(snapshotContentPoolSize);
        private ConcurrentStackPool<TransientResource> _cachedResourcePool = new(cachedResourcePoolSize);
        private TransientResource.Size _lastCachedResourceSize = new TransientResource.Size(1024, 1024);

        public SnapshotContent GetSnapshotContent()
        {
            _activeSnapshotContent.WithLabels(usage.ToString()).Inc();
            if (_snapshotPool.TryGet(out var snapshotContent))
            {
                _cachedSnapshotContent.WithLabels(usage.ToString()).Set(_snapshotPool.PooledItemCount);
                return snapshotContent;
            }

            _createdSnapshotContent.WithLabels(usage.ToString()).Inc();
            return new SnapshotContent();
        }

        public void ReturnSnapshotContent(SnapshotContent snapshotContent)
        {
            _activeSnapshotContent.WithLabels(usage.ToString()).Dec();
            if (!_snapshotPool.Return(snapshotContent))
            {
                _poolFullSnapshotContent.WithLabels(usage.ToString()).Inc();
            }

            _cachedSnapshotContent.WithLabels(usage.ToString()).Set(_snapshotPool.PooledItemCount);
        }

        public TransientResource GetCachedResource()
        {
            _activeCachedResource.WithLabels(usage.ToString()).Inc();
            if (_cachedResourcePool.TryGet(out var cachedResource))
            {
                _cachedCachedResource.WithLabels(usage.ToString()).Set(_cachedResourcePool.PooledItemCount);
                return cachedResource;
            }

            _createdCachedResource.WithLabels(usage.ToString()).Inc();
            return new TransientResource(_lastCachedResourceSize);
        }

        public void ReturnCachedResource(TransientResource transientResource)
        {
            _activeCachedResource.WithLabels(usage.ToString()).Dec();
            if (!_cachedResourcePool.Return(transientResource))
            {
                _lastCachedResourceSize = transientResource.GetSize();
                _poolFullCachedResource.WithLabels(usage.ToString()).Inc();
            }

            _cachedCachedResource.WithLabels(usage.ToString()).Set(_cachedResourcePool.PooledItemCount);
        }
    }
}
