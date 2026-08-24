// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
namespace Nethermind.Evm.State;

/// <summary>
/// Precompile result caches with 2 tiers: <br/>
/// - a per-block tier partitioned per precompile address; <br/>
/// - one surviving tier shared by every precompile.
/// </summary>
/// <remarks>
/// In the per-block tier each precompile gets its own partition with a separate byte budget.
/// A precompile filling its own partition cannot deny cache to another,
/// so cheap frequent calls cannot starve expensive-to-compute results.
/// The surviving tier is shared, so it gives no such guarantee.
/// </remarks>
public sealed class PrecompileCaches
{
    /// <summary> Accounting weight charged per entry, on top of its key and output bytes, as a container cost estimate. </summary>
    public const int EntryOverheadBytes = 160;

    /// <summary> Key+output bytes above which a result is not worth a slot in the surviving tier. </summary>
    private const int MaxSurvivingEntryBytes = 2048;

    /// <summary> Per-partition budget below which caching holds too few results to be enabled. </summary>
    private const int MinUsefulPartitionBytes = 64 * 1024;

    /// <summary> Entry count above which a partition grows on demand instead of being sized upfront. </summary>
    private const int MaxPartitionCapacity = 32 * 1024;

    /// <summary> Total budget above which the operator is warned that the cache may not fit the node. </summary>
    private const long ImplausibleTotalBytes = 1024L * 1024 * 1024;

    // Metric label values
    private const string ProbeBlockHit = "block_hit";
    private const string ProbeSurvivingHit = "surviving_hit";
    private const string ProbeMiss = "miss";
    private const string AddedToBlock = "block";
    private const string AddedToSurviving = "surviving";
    private const string RejectedFull = "rejected_full";
    private const string RejectedDuplicate = "rejected_duplicate";
    private const string RejectedTooLarge = "too_large";

    /// <summary> For flows and tests that don't cache precompile results. </summary>
    public static PrecompileCaches Empty { get; } = new([], new PreBlockCachesConfig(), maxBytes: 0);

    private readonly FrozenDictionary<AddressAsKey, Partition> _partitions;

    /// <summary> Bounded by entry count and by <see cref="MaxSurvivingEntryBytes"/>, and is never cleared. </summary>
    private readonly ClockCache<Key, Result<byte[]>> _survivingCache;

    // ReSharper disable once UnusedMember.Global - used by DI
    /// <summary> Caches the results of every precompile from <paramref name="precompileProvider"/> that supports caching. </summary>
    public PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, IBlocksConfig blocksConfig, ILogManager? logManager = null)
        : this(precompileProvider, config, blocksConfig.PrecompileCacheMaxKilobytes * 1024L, logManager) { }

    /// <summary> Byte-exact budget, bypassing <see cref="IBlocksConfig.PrecompileCacheMaxKilobytes"/>. </summary>
    internal PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
        : this(maxBytes > 0 ? CacheablePrecompiles(precompileProvider) : [], config, maxBytes, logManager) { }

    private PrecompileCaches(List<(AddressAsKey Address, string Name)> precompiles, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
    {
        // equal shares per precompile for now
        long partitionSize = precompiles.Count == 0 ? 0 : maxBytes / precompiles.Count;
        int survivingMaxEntries = precompiles.Count == 0 ? 0 : config.SurvivingPrecompileCacheMaxEntries;

        _survivingCache = new ClockCache<Key, Result<byte[]>>(survivingMaxEntries, comparer: EqualityComparer<Key>.Default);
        _partitions = precompiles.ToFrozenDictionary(
            static precompile => precompile.Address,
            precompile => new Partition(precompile.Name, partitionSize, _survivingCache)
        );

        Metrics.PrecompileCachePartitionMaxBytes = precompiles.Count == 0 ? 0 : partitionSize;

        LogCacheBudget(precompiles.Count, partitionSize, maxBytes, logManager);
    }

    private static void LogCacheBudget(int partitionCount, long partitionSize, long maxBytes, ILogManager? logManager)
    {
        ILogger logger = (logManager ?? NullLogManager.Instance).GetClassLogger<PrecompileCaches>();

        if (partitionCount == 0)
        {
            if (logger.IsTrace) logger.Trace("Precompile result caching is disabled.");
        }
        else if (partitionSize < MinUsefulPartitionBytes)
        {
            if (logger.IsWarn)
            {
                logger.Warn($"Precompile result caching is effectively off: the budget leaves {partitionSize / 1024} KB per precompile. "
                    + $"Raise {nameof(IBlocksConfig.PrecompileCacheMaxKilobytes)}, or set it to -1 to disable caching explicitly.");
            }
        }
        else if (maxBytes >= ImplausibleTotalBytes)
        {
            if (logger.IsWarn)
            {
                logger.Warn($"Precompile result cache may grow to {maxBytes / (1024 * 1024)} MB, {partitionSize / (1024 * 1024)} MB for each of "
                    + $"{partitionCount} precompiles. Check that it fits the memory budget of this node.");
            }
        }
        else if (logger.IsTrace)
        {
            logger.Trace($"Precompile result cache: {partitionSize / 1024} KB for each of {partitionCount} precompiles.");
        }
    }

    /// <summary> Entries held by the surviving tier, across every precompile. </summary>
    internal int SurvivingCacheCount => _survivingCache.Count;

    /// <summary> The per-block partition for <paramref name="address"/>, or <c>false</c> if it is not cached. </summary>
    public bool TryGetPartition(Address address, [NotNullWhen(true)] out Partition? partition) =>
        _partitions.TryGetValue(address, out partition);

    /// <summary> Total per-block entries across every partition. </summary>
    /// <remarks>
    /// Property is for tests only unless optimized - counting on <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// takes all locks inside, stopping any new admissions.
    /// </remarks>
    internal int BlockCacheCount
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
                count += partition.Value.Count;

            return count;
        }
    }

    /// <summary> Empties the per-block tier. Callers must join any concurrent warming first. </summary>
    public void ClearBlockCache()
    {
        // publishes the metrics to make occupancy gauges report each block's high point
        Metrics.PrecompileCacheSurvivingEntries = _survivingCache.Count;
        foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
        {
            partition.Value.PublishMetrics();
            partition.Value.Clear();
        }
    }

    private static List<(AddressAsKey Address, string Name)> CacheablePrecompiles(IPrecompileProvider precompileProvider)
    {
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = precompileProvider.GetPrecompiles();

        List<(AddressAsKey Address, string Name)> cacheable = new(precompiles.Count);
        foreach (KeyValuePair<AddressAsKey, CodeInfo> precompile in precompiles)
        {
            if (precompile.Value.Precompile?.SupportsCaching == true)
                cacheable.Add((precompile.Key, precompile.Value.Precompile.Name));
        }

        return cacheable;
    }

    /// <summary> One precompile's share of the per-block tier, bounded in bytes. </summary>
    /// <remarks>
    /// Admission stops at the limit instead of evicting: the worst case is that caching stops helping for the
    /// rest of the block, which is the behaviour of not caching at all.
    /// </remarks>
    public sealed class Partition
    {
        private readonly ConcurrentDictionary<Key, Result<byte[]>> _entries;

        private readonly ClockCache<Key, Result<byte[]>> _survivingCache;
        private readonly string _name;

        private long _bytes;

        // Metrics, counted in fields and published on block clear
        // to prevent additional dictionary lookup on read path
        private long _blockHits;
        private long _survivingHits;
        private long _misses;
        private long _admitted;
        private long _survivingAdmitted;
        private long _rejectedFull;
        private long _rejectedDuplicate;
        private long _tooLarge;

        internal int Count => _entries.Count;

        internal long MaxBytes { get; }

        /// <summary> Bytes reserved for this partition. </summary>
        /// <remarks>
        /// Admission reserves before it checks, so this may read above <see cref="MaxBytes"/> while an over-the-limit entry is being processed.
        /// </remarks>
        internal long UsedBytes => Volatile.Read(ref _bytes);

        internal Partition(string name, long maxBytes, ClockCache<Key, Result<byte[]>> survivingCache)
        {
            // prefer partition to never resize - resizing takes locks on the whole dictionary
            int maxEntries = (int)Math.Min(maxBytes / EntryOverheadBytes, MaxPartitionCapacity);

            _entries = new ConcurrentDictionary<Key, Result<byte[]>>(CollectionExtensions.LockPartitions, maxEntries);
            _name = name;
            MaxBytes = maxBytes;
            _survivingCache = survivingCache;
        }

        /// <summary> Looks <paramref name="key"/> up in this partition, then in the surviving tier. </summary>
        public bool TryGet(in Key key, out Result<byte[]> result)
        {
            if (_entries.TryGetValue(key, out result))
            {
                Record(ref _blockHits);
                return true;
            }

            if (_survivingCache.TryGet(key, out result))
            {
                Record(ref _survivingHits);
                return true;
            }

            Record(ref _misses);
            return false;
        }

        /// <summary> Stores <paramref name="result"/> under a data-owning copy of <paramref name="key"/>, if the partition has room for it. </summary>
        /// <remarks> Reserves before checking, so a concurrent reservation near the limit can refuse an entry the partition had room for. </remarks>
        public bool TryAdd(in Key key, Result<byte[]> result)
        {
            long entryBytes = (long)key.DataLength + (result.Data?.Length ?? 0);
            long reservation = entryBytes + EntryOverheadBytes;

            if (Interlocked.Add(ref _bytes, reservation) > MaxBytes)
            {
                Interlocked.Add(ref _bytes, -reservation);
                Record(ref _rejectedFull);
                return false;
            }

            // we need to rebuild the key with data copy as the data can be changed by VM processing
            // effective-input bounds are expected to remain the same
            Key copiedKey = key.WithCopiedData();
            if (!_entries.TryAdd(copiedKey, result))
            {
                // another thread computed the same result concurrently - this copy is redundant
                Interlocked.Add(ref _bytes, -reservation);
                Record(ref _rejectedDuplicate);
                return false;
            }

            Record(ref _admitted);
            if (entryBytes <= MaxSurvivingEntryBytes)
            {
                _survivingCache.Set(copiedKey, result);
                Record(ref _survivingAdmitted);
            }
            else
            {
                Record(ref _tooLarge);
            }

            return true;
        }

        internal void Clear()
        {
            _entries.NoLockClear();
            Volatile.Write(ref _bytes, 0);
        }

        /// <summary> Copies this partition's counters into the exported metrics. </summary>
        internal void PublishMetrics()
        {
            if (!ExecutionMetricsFlag.IsActive) return;

            Metrics.PrecompileCacheProbes[(_name, ProbeBlockHit)] = Volatile.Read(ref _blockHits);
            Metrics.PrecompileCacheProbes[(_name, ProbeSurvivingHit)] = Volatile.Read(ref _survivingHits);
            Metrics.PrecompileCacheProbes[(_name, ProbeMiss)] = Volatile.Read(ref _misses);
            Metrics.PrecompileCacheAdds[(_name, AddedToBlock)] = Volatile.Read(ref _admitted);
            Metrics.PrecompileCacheAdds[(_name, AddedToSurviving)] = Volatile.Read(ref _survivingAdmitted);
            Metrics.PrecompileCacheAdds[(_name, RejectedFull)] = Volatile.Read(ref _rejectedFull);
            Metrics.PrecompileCacheAdds[(_name, RejectedDuplicate)] = Volatile.Read(ref _rejectedDuplicate);
            Metrics.PrecompileCacheAdds[(_name, RejectedTooLarge)] = Volatile.Read(ref _tooLarge);
            Metrics.PrecompileCacheUsedBytes[_name] = UsedBytes;
            Metrics.PrecompileCacheEntries[_name] = Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Record(ref long counter)
        {
            if (!ExecutionMetricsFlag.IsActive) return;
            Interlocked.Increment(ref counter);
        }
    }

    /// <summary> Key combining precompile address, its effective input, and the fork it ran under. </summary>
    public readonly struct Key(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec) : IEquatable<Key>
    {
        // Surviving tier is shared and needs a discriminator
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        // Reference-compared; results may differ across forks, so entries never cross a fork boundary.
        private IReleaseSpec Spec { get; } = spec;

        internal int DataLength => Data.Length;

        /// <summary> Creates a copy that owns its data. </summary>
        internal Key WithCopiedData() => new(Address, Data.ToArray(), Spec);

        public bool Equals(Key other) => ReferenceEquals(Spec, other.Spec) && Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode() ^ RuntimeHelpers.GetHashCode(Spec);
        public static bool operator ==(Key left, Key right) => left.Equals(right);
        public static bool operator !=(Key left, Key right) => !(left == right);
    }
}
