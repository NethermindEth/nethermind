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
/// Pools the per-layer collections, sized per <see cref="Usage"/> so that a wide compacted layer's
/// content never lands in the pool a per-block scope rents from.
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
            { Usage.MainBlockProcessing, new ResourcePoolCategory(Usage.MainBlockProcessing, config.CompactSize + 8) },

            // read-only here means never committed to the repository; the scope may still commit locally
            { Usage.ReadOnlyProcessingEnv, new ResourcePoolCategory(Usage.ReadOnlyProcessingEnv, Environment.ProcessorCount * 4) },

            // one compaction runs at a time per width, and its content is dropped as soon as it is written
            { Usage.Compact2, new ResourcePoolCategory(Usage.Compact2, 2) },
            { Usage.Compact4, new ResourcePoolCategory(Usage.Compact4, 2) },
            { Usage.Compact8, new ResourcePoolCategory(Usage.Compact8, 2) },
            { Usage.Compact16, new ResourcePoolCategory(Usage.Compact16, 2) },
            { Usage.Compact32, new ResourcePoolCategory(Usage.Compact32, 2) },
            { Usage.Compact64, new ResourcePoolCategory(Usage.Compact64, 2) },
            { Usage.Compact128, new ResourcePoolCategory(Usage.Compact128, 2) },
            { Usage.Compact256, new ResourcePoolCategory(Usage.Compact256, 2) },
            { Usage.Compact512, new ResourcePoolCategory(Usage.Compact512, 2) },
            { Usage.Compact1024, new ResourcePoolCategory(Usage.Compact1024, 2) },
            { Usage.Compact2048, new ResourcePoolCategory(Usage.Compact2048, 2) },
        };

    public PbtSnapshotContent GetSnapshotContent(Usage usage) => _categories[usage].GetSnapshotContent();

    public void ReturnSnapshotContent(Usage usage, PbtSnapshotContent content) => _categories[usage].ReturnSnapshotContent(content);

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

    // a stack rather than a queue: the most recently returned item is the likeliest to still be in cache
    private class ConcurrentStackPool<T>(int maxCapacity) where T : notnull, IDisposable, IResettable
    {
        private readonly ConcurrentStack<T> _pool = new();

        public int PooledItemCount => _pool.Count;

        public bool TryGet([NotNullWhen(true)] out T? item) => _pool.TryPop(out item);

        /// <summary>Resets <paramref name="item"/> and pools it, or disposes it when the pool is full.</summary>
        /// <returns>Whether the item was pooled.</returns>
        public bool Return(T item)
        {
            // reset before the capacity check: an item dropped on overflow must still release
            // whatever it holds
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

    private class ResourcePoolCategory(Usage usage, int snapshotContentPoolSize)
    {
        private readonly ConcurrentStackPool<PbtSnapshotContent> _snapshotPool = new(snapshotContentPoolSize);
        private readonly PooledResourceLabel _snapshotLabel = new(usage.ToString(), nameof(PbtSnapshotContent));

        public PbtSnapshotContent GetSnapshotContent()
        {
            Metrics.ActivePooledResource.AddBy(_snapshotLabel, 1);
            if (_snapshotPool.TryGet(out PbtSnapshotContent? content))
            {
                Metrics.CachedPooledResource[_snapshotLabel] = _snapshotPool.PooledItemCount;
                return content;
            }

            // the only signal of a pool sized too small for its category: it never stops climbing
            Metrics.CreatedPooledResource.AddBy(_snapshotLabel, 1);
            return new PbtSnapshotContent();
        }

        public void ReturnSnapshotContent(PbtSnapshotContent content)
        {
            Metrics.ActivePooledResource.AddBy(_snapshotLabel, -1);
            _snapshotPool.Return(content);
            Metrics.CachedPooledResource[_snapshotLabel] = _snapshotPool.PooledItemCount;
        }
    }

    public record PooledResourceLabel(string Category, string ResourceType) : IMetricLabels
    {
        public string[] Labels => [Category, ResourceType];
    }
}
