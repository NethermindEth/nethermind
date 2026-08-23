// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

/// <summary>
/// Precompile result caches with 2 tiers: <br/>
/// - a per-block tier partitioned per precompile address <br/>
/// - one surviving tier shared by every precompile.
/// </summary>
/// <remarks>
/// Each precompile gets its own partition with a separate byte budget.
/// A precompile filling its own partition cannot deny cache to another,
/// so cheap frequent calls cannot starve expensive-to-compute results.
/// </remarks>
public sealed class PrecompileCaches
{
    /// <summary> Accounting weight charged per entry, on top of its key and output bytes, as a container cost estimate. </summary>
    public const int EntryOverheadBytes = 160;

    /// <summary> Key+output bytes above which a result is not worth a slot in the surviving tier. </summary>
    private const int MaxSurvivingEntryBytes = 2048;

    /// <summary> Initial capacity for every precompile cache partition. </summary>
    private const int PartitionInitialCapacity = 1024;

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
    public PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, IBlocksConfig blocksConfig)
        : this(precompileProvider, config, blocksConfig.PrecompileCacheMaxKilobytes * 1024L) { }

    /// <summary> Byte-exact budget, bypassing <see cref="IBlocksConfig.PrecompileCacheMaxKilobytes"/>. </summary>
    public PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, long maxBytes)
        : this(maxBytes > 0 ? CacheablePrecompiles(precompileProvider) : [], config, maxBytes) { }

    private PrecompileCaches(List<(AddressAsKey Address, string Name)> precompiles, PreBlockCachesConfig config, long maxBytes)
    {
        // equal shares per precompile for now
        long partitionSize = precompiles.Count == 0 ? 0 : maxBytes / precompiles.Count;
        int survivingMaxEntries = precompiles.Count == 0 ? 0 : config.SurvivingPrecompileCacheMaxEntries;
        _survivingCache = new ClockCache<Key, Result<byte[]>>(survivingMaxEntries, comparer: EqualityComparer<Key>.Default);

        Dictionary<AddressAsKey, Partition> partitions = new(precompiles.Count);
        foreach ((AddressAsKey address, string name) in precompiles)
            partitions[address] = new Partition(name, partitionSize, _survivingCache);

        _partitions = partitions.ToFrozenDictionary();
        Metrics.PrecompileCachePartitionMaxBytes = precompiles.Count == 0 ? 0 : partitionSize;
    }

    /// <summary> Entries held by the surviving tier, across every precompile. </summary>
    public int SurvivingCacheCount => _survivingCache.Count;

    /// <summary> The per-block partition for <paramref name="address"/>, or <c>false</c> if it is not cached. </summary>
    public bool TryGetPartition(Address address, [NotNullWhen(true)] out Partition? partition) =>
        _partitions.TryGetValue(address, out partition);

    /// <summary> Total per-block entries across every partition. </summary>
    public int BlockCacheCount => _partitions.Sum(static partition => partition.Value.Count);

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
        List<(AddressAsKey, string)> cacheable = new(precompiles.Count);
        foreach (KeyValuePair<AddressAsKey, CodeInfo> precompile in precompiles)
        {
            IPrecompile? implementation = precompile.Value.Precompile;
            if (implementation?.SupportsCaching == true)
                cacheable.Add((precompile.Key, GetName(implementation)));
        }

        return cacheable;
    }

    private static string GetName(IPrecompile precompile)
    {
        Type implementation = precompile.GetType();
        object? declaredName = implementation.GetProperty(nameof(IPrecompile.Name), BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return declaredName is string { Length: > 0 } name ? name : implementation.Name;
    }

    /// <summary>One precompile's share of the per-block tier, bounded in bytes.</summary>
    /// <remarks>
    /// Admission stops at the limit instead of evicting: the worst case is that caching stops helping for the
    /// rest of the block, which is the behaviour of not caching at all.
    /// </remarks>
    public sealed class Partition
    {
        private readonly ConcurrentDictionary<Key, Result<byte[]>> _entries =
            new(CollectionExtensions.LockPartitions, PartitionInitialCapacity);

        private readonly ClockCache<Key, Result<byte[]>> _survivingCache;

        private readonly string _name;

        private long _bytes;

        // Counted in fields rather than straight into the labelled metrics: a dictionary write costs two string
        // hashes and a CAS loop, which is a large fraction of the cache-hit path itself. Published on block clear.
        private long _blockHits;
        private long _survivingHits;
        private long _misses;
        private long _admitted;
        private long _survivingAdmitted;
        private long _rejectedFull;
        private long _rejectedDuplicate;
        private long _tooLarge;

        public long MaxBytes { get; }
        public long UsedBytes => Volatile.Read(ref _bytes);
        internal int Count => _entries.Count;

        internal Partition(string name, long maxBytes, ClockCache<Key, Result<byte[]>> survivingCache)
        {
            _name = name;
            MaxBytes = maxBytes;
            _survivingCache = survivingCache;
        }

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

        /// <summary> Stores <paramref name="result"/> under a data-owning copy of <paramref name="key"/>, in whichever tiers accept it. </summary>
        public void TryAdd(in Key key, Result<byte[]> result)
        {
            long entryBytes = (long)key.DataLength + (result.Data?.Length ?? 0);
            bool wantSurviving = entryBytes <= MaxSurvivingEntryBytes;
            if (!wantSurviving)
            {
                Record(ref _tooLarge);
            }

            long reservation = entryBytes + EntryOverheadBytes;
            bool wantBlock = Interlocked.Add(ref _bytes, reservation) <= MaxBytes;
            if (!wantBlock)
            {
                Interlocked.Add(ref _bytes, -reservation);
                Record(ref _rejectedFull);
            }

            if (!wantBlock && !wantSurviving) return;

            // we need to rebuild the key with data copy as the data can be changed by VM processing
            // effective-input bounds are expected to remain the same
            Key copiedKey = key.WithCopiedData();
            if (wantBlock)
            {
                if (_entries.TryAdd(copiedKey, result))
                {
                    Record(ref _admitted);
                }
                else
                {
                    // another thread computed the same result concurrently - this copy is redundant
                    Interlocked.Add(ref _bytes, -reservation);
                    Record(ref _rejectedDuplicate);
                }
            }

            if (wantSurviving)
            {
                _survivingCache.Set(copiedKey, result);
                Record(ref _survivingAdmitted);
            }
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

    public readonly struct Key(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec) : IEquatable<Key>
    {
        // Surviving tier is shared and needs a discriminator
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        // Reference-compared; results may differ across forks, so entries never cross a fork boundary.
        private IReleaseSpec Spec { get; } = spec;

        internal int DataLength => Data.Length;

        /// <summary> Creates a copy that owns its data. </summary>
        public Key WithCopiedData() => new(Address, Data.ToArray(), Spec);

        public bool Equals(Key other) => ReferenceEquals(Spec, other.Spec) && Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode() ^ RuntimeHelpers.GetHashCode(Spec);
        public static bool operator ==(Key left, Key right) => left.Equals(right);
        public static bool operator !=(Key left, Key right) => !(left == right);
    }
}
