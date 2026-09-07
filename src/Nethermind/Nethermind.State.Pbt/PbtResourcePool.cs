// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// Pool sized per <see cref="Usage"/> so that a wide compacted layer's content never lands in the
/// pool a per-block scope rents from.
/// </summary>
public class PbtResourcePool : IPbtResourcePool
{
    private readonly Dictionary<Usage, ResourcePoolCategory> _categories;

    public PbtResourcePool(IPbtConfig config) =>
        _categories = new()
        {
            // A persisted segment returns its whole chain at once, so the pool must absorb that burst:
            // PersistSegment prunes CompactSize canonical layers in one go (everything older went with
            // the previous segment), plus the fork siblings pruned alongside them. Catch-up drains and
            // the finality-stall backstop overflow this by design and refill over the next segment.
            { Usage.MainBlockProcessing, new ResourcePoolCategory(Usage.MainBlockProcessing, config.CompactSize + 8, 2) },

            // Read-only means never committed to the repository; the scope may still commit locally.
            { Usage.ReadOnlyProcessingEnv, new ResourcePoolCategory(Usage.ReadOnlyProcessingEnv, Environment.ProcessorCount * 4, Environment.ProcessorCount * 4) },

            // one compaction runs at a time per width, and its content is dropped as soon as it is written
            { Usage.Compact2, new ResourcePoolCategory(Usage.Compact2, 2, 0) },
            { Usage.Compact4, new ResourcePoolCategory(Usage.Compact4, 2, 0) },
            { Usage.Compact8, new ResourcePoolCategory(Usage.Compact8, 2, 0) },
            { Usage.Compact16, new ResourcePoolCategory(Usage.Compact16, 2, 0) },
            { Usage.Compact32, new ResourcePoolCategory(Usage.Compact32, 2, 0) },
            { Usage.Compact64, new ResourcePoolCategory(Usage.Compact64, 2, 0) },
            { Usage.Compact128, new ResourcePoolCategory(Usage.Compact128, 2, 0) },
            { Usage.Compact256, new ResourcePoolCategory(Usage.Compact256, 2, 0) },
            { Usage.Compact512, new ResourcePoolCategory(Usage.Compact512, 2, 0) },
            { Usage.Compact1024, new ResourcePoolCategory(Usage.Compact1024, 2, 0) },
            { Usage.Compact2048, new ResourcePoolCategory(Usage.Compact2048, 2, 0) },
        };

    public PbtSnapshotContent GetSnapshotContent(Usage usage) => _categories[usage].GetSnapshotContent();

    public void ReturnSnapshotContent(Usage usage, PbtSnapshotContent content) => _categories[usage].ReturnSnapshotContent(content);

    /// <inheritdoc/>
    public ShardedWriteBatch GetWriteBatch(Usage usage) => _categories[usage].GetWriteBatch();

    /// <inheritdoc/>
    public void ReturnWriteBatch(Usage usage, ShardedWriteBatch batch) => _categories[usage].ReturnWriteBatch(batch);

    /// <inheritdoc/>
    public PbtTransientResource GetCachedResource(Usage usage)
    {
        PbtTransientResource resource = _categories[usage].GetCachedResource();
        resource.OnRented(this, usage);
        return resource;
    }

    /// <inheritdoc/>
    public void ReturnCachedResource(Usage usage, PbtTransientResource resource) => _categories[usage].ReturnCachedResource(resource);

    /// <summary>Maps a merged layer's width to its size class, rounded up to the next pooled power of two.</summary>
    /// <remarks>
    /// Takes the width actually merged, never the configured compact size: a segment is also persisted
    /// at depth 1 on a genesis flush and at up to the reorg depth by the finality-stall backstop.
    /// </remarks>
    public static Usage CompactUsage(int mergedLayerCount) => (uint)BitOperations.RoundUpToPowerOf2((uint)mergedLayerCount) switch
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
        _ => Usage.Compact2048,
    };

    public enum Usage
    {
        MainBlockProcessing,
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

    // A stack favors recently returned, cache-resident items.
    private class ConcurrentStackPool<T>(int maxCapacity) where T : notnull, IDisposable, IResettable
    {
        private readonly ConcurrentStack<T> _pool = new();

        public int PooledItemCount => _pool.Count;

        public bool TryGet([NotNullWhen(true)] out T? item) => _pool.TryPop(out item);

        public bool Return(T item)
        {
            // Reset before the capacity check so an overflowed item releases its contents.
            item.Reset();
            if (_pool.Count >= maxCapacity)
            {
                item.Dispose();
                return false;
            }

            _pool.Push(item);
            return true;
        }
    }

    private class ResourcePoolCategory(Usage usage, int snapshotContentPoolSize, int writableBundlePoolSize)
    {
        private readonly ConcurrentStackPool<PbtSnapshotContent> _snapshotPool = new(snapshotContentPoolSize);
        // Each writable bundle holds three partition batches and one prewarm resource.
        private readonly ConcurrentStackPool<ShardedWriteBatch> _writeBatchPool = new(writableBundlePoolSize * 3);
        private readonly PooledResourceLabel _writeBatchLabel = new(usage.ToString(), nameof(ShardedWriteBatch));
        private readonly ConcurrentStackPool<PbtTransientResource> _cachedResourcePool = new(writableBundlePoolSize);
        private long _lastCachedResourceCapacity = 1024;
        private readonly PooledResourceLabel _cachedResourceLabel = new(usage.ToString(), nameof(PbtTransientResource));
        private readonly PooledResourceLabel _snapshotLabel = new(usage.ToString(), nameof(PbtSnapshotContent));

        public PbtSnapshotContent GetSnapshotContent()
        {
            Metrics.PbtActivePooledResource.AddBy(_snapshotLabel, 1);
            if (_snapshotPool.TryGet(out PbtSnapshotContent? content))
            {
                Metrics.PbtCachedPooledResource[_snapshotLabel] = _snapshotPool.PooledItemCount;
                return content;
            }

            // This grows indefinitely when the category's pool is too small.
            Metrics.PbtCreatedPooledResource.AddBy(_snapshotLabel, 1);
            return new PbtSnapshotContent();
        }

        public void ReturnSnapshotContent(PbtSnapshotContent content)
        {
            Metrics.PbtActivePooledResource.AddBy(_snapshotLabel, -1);
            _snapshotPool.Return(content);
            Metrics.PbtCachedPooledResource[_snapshotLabel] = _snapshotPool.PooledItemCount;
        }

        public ShardedWriteBatch GetWriteBatch()
        {
            Metrics.PbtActivePooledResource.AddBy(_writeBatchLabel, 1);
            if (_writeBatchPool.TryGet(out ShardedWriteBatch? batch))
            {
                Metrics.PbtCachedPooledResource[_writeBatchLabel] = _writeBatchPool.PooledItemCount;
                return batch;
            }
            Metrics.PbtCreatedPooledResource.AddBy(_writeBatchLabel, 1);
            return new ShardedWriteBatch();
        }

        public void ReturnWriteBatch(ShardedWriteBatch batch)
        {
            Metrics.PbtActivePooledResource.AddBy(_writeBatchLabel, -1);
            _writeBatchPool.Return(batch);
            Metrics.PbtCachedPooledResource[_writeBatchLabel] = _writeBatchPool.PooledItemCount;
        }

        public PbtTransientResource GetCachedResource()
        {
            Metrics.PbtActivePooledResource.AddBy(_cachedResourceLabel, 1);
            if (_cachedResourcePool.TryGet(out PbtTransientResource? resource))
            {
                Metrics.PbtCachedPooledResource[_cachedResourceLabel] = _cachedResourcePool.PooledItemCount;
                return resource;
            }

            Metrics.PbtCreatedPooledResource.AddBy(_cachedResourceLabel, 1);
            return new PbtTransientResource(Volatile.Read(ref _lastCachedResourceCapacity));
        }

        public void ReturnCachedResource(PbtTransientResource resource)
        {
            Metrics.PbtActivePooledResource.AddBy(_cachedResourceLabel, -1);
            if (!_cachedResourcePool.Return(resource))
                Volatile.Write(ref _lastCachedResourceCapacity, resource.Capacity);
            Metrics.PbtCachedPooledResource[_cachedResourceLabel] = _cachedResourcePool.PooledItemCount;
        }

    }

    public record PooledResourceLabel(string Category, string ResourceType) : IMetricLabels
    {
        public string[] Labels => [Category, ResourceType];
    }
}
