// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.Db;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// A pool of objects used to manage different sized pool objects for different category.
/// </summary>
/// <param name="flatConfig"></param>
public class ResourcePool(IFlatDbConfig flatConfig) : IResourcePool
{
    private readonly Dictionary<Usage, ResourcePoolCategory> _categories = new()
    {
        // For main BlockProcessing once a compacted snapshot is persisted, all `flatConfig.CompactSize` snapshot content will be returned.
        { Usage.MainBlockProcessing, new ResourcePoolCategory(Usage.MainBlockProcessing, flatConfig.CompactSize + 8, 2) },

        // PostMainBlockProcessing is a special usage right after the commit of `MainBlockProcessing` which only commit once and never modified.
        { Usage.PostMainBlockProcessing, new ResourcePoolCategory(Usage.PostMainBlockProcessing, 1, 1) },

        // Note: prewarmer use readonly processing env
        // Note: readonly here means it's never committed to the flat repo, but within the worldscope itself it may be committed.
        { Usage.ReadOnlyProcessingEnv, new ResourcePoolCategory(Usage.ReadOnlyProcessingEnv, Environment.ProcessorCount * 4, Environment.ProcessorCount * 4) },

        // Per-power-of-2 compact pools. Each level only has ~1 active snapshot at a time.
        { Usage.Compact2, new ResourcePoolCategory(Usage.Compact2, 2, 1) },
        { Usage.Compact4, new ResourcePoolCategory(Usage.Compact4, 2, 1) },
        { Usage.Compact8, new ResourcePoolCategory(Usage.Compact8, 2, 1) },
        { Usage.Compact16, new ResourcePoolCategory(Usage.Compact16, 2, 1) },
        { Usage.Compact32, new ResourcePoolCategory(Usage.Compact32, 2, 1) },
        { Usage.Compact64, new ResourcePoolCategory(Usage.Compact64, 2, 1) },
        { Usage.Compact128, new ResourcePoolCategory(Usage.Compact128, 2, 1) },
        { Usage.Compact256, new ResourcePoolCategory(Usage.Compact256, 2, 1) },
        { Usage.Compact512, new ResourcePoolCategory(Usage.Compact512, 2, 1) },
        { Usage.Compact1024, new ResourcePoolCategory(Usage.Compact1024, 2, 1) },
        { Usage.Compact2048, new ResourcePoolCategory(Usage.Compact2048, 2, 1) },
    };

    public SnapshotContent GetSnapshotContent(Usage usage) => _categories[usage].GetSnapshotContent();

    public void ReturnSnapshotContent(Usage usage, SnapshotContent snapshotContent) => _categories[usage].ReturnSnapshotContent(snapshotContent);

    public TransientResource GetCachedResource(Usage usage) => _categories[usage].GetCachedResource();

    public void ReturnCachedResource(Usage usage, TransientResource transientResource) => _categories[usage].ReturnCachedResource(transientResource);

    public static Usage CompactUsage(int compactSize) => compactSize switch
    {
        <= 2 => Usage.Compact2,
        4 => Usage.Compact4,
        8 => Usage.Compact8,
        16 => Usage.Compact16,
        32 => Usage.Compact32,
        64 => Usage.Compact64,
        128 => Usage.Compact128,
        256 => Usage.Compact256,
        512 => Usage.Compact512,
        1024 => Usage.Compact1024,
        >= 2048 => Usage.Compact2048,
        _ => throw new ArgumentOutOfRangeException(nameof(compactSize))
    };

    public Snapshot CreateSnapshot(in StateId from, in StateId to, Usage usage) =>
        new(
            from,
            to,
            content: GetSnapshotContent(usage),
            resourcePool: this,
            usage: usage);

    public enum Usage
    {
        MainBlockProcessing,
        PostMainBlockProcessing,
        ReadOnlyProcessingEnv,
        Compact2,
        Compact4,
        Compact8,
        Compact16,
        Compact32,
        Compact64,
        Compact128,
        Compact256,
        Compact512,
        Compact1024,
        Compact2048,
    }

    // Using stack for better cpu cache effectiveness
    private class ConcurrentStackPool<T>(int maxCapacity = 16) where T : notnull, IDisposable, IResettable
    {
        private readonly ConcurrentStack<T> _queue = new();
        public double PooledItemCount => _queue.Count;

        public bool TryGet([NotNullWhen(true)] out T? item) => _queue.TryPop(out item);

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
        private readonly ConcurrentStackPool<SnapshotContent> _snapshotPool = new(snapshotContentPoolSize);
        private readonly ConcurrentStackPool<TransientResource> _cachedResourcePool = new(cachedResourcePoolSize);
        private TransientResource.Size _lastCachedResourceSize = new(1024, 1024);
        private readonly PooledResourceLabel _snapshotLabel = new(usage.ToString(), "SnapshotContent");
        private readonly PooledResourceLabel _cachedResourceLabel = new(usage.ToString(), "CachedResource");

        public SnapshotContent GetSnapshotContent()
        {
            Metrics.ActivePooledResource.AddBy(_snapshotLabel, 1);
            if (_snapshotPool.TryGet(out SnapshotContent? snapshotContent))
            {
                Metrics.CachedPooledResource[_snapshotLabel] = (long)_snapshotPool.PooledItemCount;
                return snapshotContent;
            }

            Metrics.CreatedPooledResource.AddBy(_snapshotLabel, 1);
            return new SnapshotContent();
        }

        public void ReturnSnapshotContent(SnapshotContent snapshotContent)
        {
            Metrics.ActivePooledResource.AddBy(_snapshotLabel, -1);
            if (!_snapshotPool.Return(snapshotContent))
            {
            }

            Metrics.CachedPooledResource[_snapshotLabel] = (long)_snapshotPool.PooledItemCount;
        }

        public TransientResource GetCachedResource()
        {
            Metrics.ActivePooledResource.AddBy(_cachedResourceLabel, 1);
            if (_cachedResourcePool.TryGet(out TransientResource? cachedResource))
            {
                Metrics.CachedPooledResource[_cachedResourceLabel] = (long)_cachedResourcePool.PooledItemCount;
                return cachedResource;
            }

            Metrics.CreatedPooledResource.AddBy(_cachedResourceLabel, 1);
            return new TransientResource(_lastCachedResourceSize);
        }

        public void ReturnCachedResource(TransientResource transientResource)
        {
            Metrics.ActivePooledResource.AddBy(_cachedResourceLabel, -1);
            if (!_cachedResourcePool.Return(transientResource))
            {
                _lastCachedResourceSize = transientResource.GetSize();
            }

            Metrics.CachedPooledResource[_cachedResourceLabel] = (long)_cachedResourcePool.PooledItemCount;
        }
    }

    public record PooledResourceLabel(string Category, string ResourceType) : IMetricLabels
    {
        public string[] Labels => [Category, ResourceType];
    }
}
