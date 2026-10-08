// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// Pool sized per <see cref="Usage"/> so that a wide compacted layer's content never lands in the
/// pool a per-block scope rents from.
/// </summary>
public class PbtResourcePool : IPbtResourcePool
{
    private readonly Dictionary<Usage, ResourcePoolCategory> _categories;

    public PbtResourcePool(IPbtConfig config)
    {
        _categories = new()
        {
            // A persisted segment returns its whole chain at once, so the pool must absorb that burst:
            // PersistSegment prunes CompactSize canonical layers in one go (everything older went with
            // the previous segment), plus the fork siblings pruned alongside them. Catch-up drains and
            // the finality-stall backstop overflow this by design and refill over the next segment.
            { Usage.MainBlockProcessing, new ResourcePoolCategory(Usage.MainBlockProcessing, config.CompactSize + 8, 2) },

            // Read-only means never committed to the repository; the scope may still commit locally.
            { Usage.ReadOnlyProcessingEnv, new ResourcePoolCategory(Usage.ReadOnlyProcessingEnv, Environment.ProcessorCount * 4, Environment.ProcessorCount * 4) },
        };

        // one compaction runs at a time per width, and its content is dropped as soon as it is written
        for (Usage usage = Usage.Compact2; usage <= Usage.Compact2048; usage++) _categories[usage] = new ResourcePoolCategory(usage, 2, 0);
    }

    public PbtSnapshotContent GetSnapshotContent(Usage usage) => _categories[usage].GetSnapshotContent();

    public void ReturnSnapshotContent(Usage usage, PbtSnapshotContent content) => _categories[usage].ReturnSnapshotContent(content);

    /// <inheritdoc/>
    public PbtWriteBatchBuilder<PbtPath> GetWriteBatch(Usage usage) => _categories[usage].GetWriteBatch();

    /// <inheritdoc/>
    public void ReturnWriteBatch(Usage usage, PbtWriteBatchBuilder<PbtPath> batch) => _categories[usage].ReturnWriteBatch(batch);

    /// <inheritdoc/>
    public PbtWriteBatchBuilder<PbtStoragePath> GetStorageWriteBatch(Usage usage) => _categories[usage].GetStorageWriteBatch();

    /// <inheritdoc/>
    public void ReturnStorageWriteBatch(Usage usage, PbtWriteBatchBuilder<PbtStoragePath> batch) => _categories[usage].ReturnStorageWriteBatch(batch);

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
    /// Takes the number of layers in the leased window, not the scheduled width: the window mixes already-compacted
    /// and base layers, so its count can be below the width and is not necessarily a power of two.
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

    private class ResourcePoolCategory(Usage usage, int snapshotContentPoolSize, int writableBundlePoolSize)
    {
        private readonly ResourcePool.ConcurrentStackPool<PbtSnapshotContent> _snapshotPool = new(snapshotContentPoolSize);
        // Each writable bundle holds three partition batches and one transient resource.
        private readonly ResourcePool.ConcurrentStackPool<PbtWriteBatchBuilder<PbtPath>> _writeBatchPool = new(writableBundlePoolSize * 2);
        private readonly ResourcePool.ConcurrentStackPool<PbtWriteBatchBuilder<PbtStoragePath>> _storageWriteBatchPool = new(writableBundlePoolSize);
        private readonly ResourcePool.PooledResourceLabel _writeBatchLabel = new(usage.ToString(), "PbtWriteBatchBuilder");
        private readonly ResourcePool.ConcurrentStackPool<PbtTransientResource> _cachedResourcePool = new(writableBundlePoolSize);
        private int _lastNodeGroupCapacity = 1024;
        private readonly ResourcePool.PooledResourceLabel _cachedResourceLabel = new(usage.ToString(), nameof(PbtTransientResource));
        private readonly ResourcePool.PooledResourceLabel _snapshotLabel = new(usage.ToString(), nameof(PbtSnapshotContent));

        public PbtSnapshotContent GetSnapshotContent() =>
            TryRent(_snapshotPool, _snapshotLabel, out PbtSnapshotContent? content) ? content : new PbtSnapshotContent();

        public void ReturnSnapshotContent(PbtSnapshotContent content) => Return(_snapshotPool, _snapshotLabel, content);

        public PbtWriteBatchBuilder<PbtPath> GetWriteBatch() =>
            TryRent(_writeBatchPool, _writeBatchLabel, out PbtWriteBatchBuilder<PbtPath>? batch) ? batch : new PbtWriteBatchBuilder<PbtPath>();

        public void ReturnWriteBatch(PbtWriteBatchBuilder<PbtPath> batch) => Return(_writeBatchPool, _writeBatchLabel, batch);

        public PbtWriteBatchBuilder<PbtStoragePath> GetStorageWriteBatch() =>
            TryRent(_storageWriteBatchPool, _writeBatchLabel, out PbtWriteBatchBuilder<PbtStoragePath>? batch) ? batch : new PbtWriteBatchBuilder<PbtStoragePath>();

        public void ReturnStorageWriteBatch(PbtWriteBatchBuilder<PbtStoragePath> batch) => Return(_storageWriteBatchPool, _writeBatchLabel, batch);

        public PbtTransientResource GetCachedResource() =>
            TryRent(_cachedResourcePool, _cachedResourceLabel, out PbtTransientResource? resource) ? resource : new PbtTransientResource(Volatile.Read(ref _lastNodeGroupCapacity));

        public void ReturnCachedResource(PbtTransientResource resource)
        {
            if (!Return(_cachedResourcePool, _cachedResourceLabel, resource))
                Volatile.Write(ref _lastNodeGroupCapacity, resource.NodeGroups.Capacity);
        }

        /// <summary>Pops a pooled item; on a miss the caller creates one, counted as created.</summary>
        private bool TryRent<T>(ResourcePool.ConcurrentStackPool<T> pool, ResourcePool.PooledResourceLabel label, [NotNullWhen(true)] out T? item) where T : class, IDisposable, IResettable
        {
            Metrics.PbtActivePooledResource.AddBy(label, 1);
            if (pool.TryGet(out item))
            {
                Metrics.PbtCachedPooledResource[label] = CachedCount(pool, label);
                return true;
            }

            // This grows indefinitely when the category's pool is too small.
            Metrics.PbtCreatedPooledResource.AddBy(label, 1);
            return false;
        }

        /// <returns>Whether the pool kept <paramref name="item"/> rather than disposing it on overflow.</returns>
        private bool Return<T>(ResourcePool.ConcurrentStackPool<T> pool, ResourcePool.PooledResourceLabel label, T item) where T : class, IDisposable, IResettable
        {
            Metrics.PbtActivePooledResource.AddBy(label, -1);
            bool kept = pool.Return(item);
            Metrics.PbtCachedPooledResource[label] = CachedCount(pool, label);
            return kept;
        }

        // The two write-batch pools share one label, so its count covers both.
        private long CachedCount<T>(ResourcePool.ConcurrentStackPool<T> pool, ResourcePool.PooledResourceLabel label) where T : class, IDisposable, IResettable =>
            (long)(ReferenceEquals(label, _writeBatchLabel) ? _writeBatchPool.PooledItemCount + _storageWriteBatchPool.PooledItemCount : pool.PooledItemCount);
    }
}
