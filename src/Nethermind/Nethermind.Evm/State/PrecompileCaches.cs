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
public sealed partial class PrecompileCaches
{
    /// <summary> Accounting weight charged per entry, on top of its key and output bytes, as a container cost estimate. </summary>
    internal const int EntryOverheadBytes = 160;

    /// <summary> Key+output bytes above which a result is not worth a slot in the surviving tier. </summary>
    internal const int MaxSurvivingEntryBytes = 2048;

    /// <summary> Entry count above which a partition grows on demand instead of being sized upfront. </summary>
    private const int MaxPartitionCapacity = 32 * 1024;

    /// <summary> Total budget above which the operator is warned that the cache may not fit the node. </summary>
    private const long ImplausibleTotalBytes = 1024L * 1024 * 1024;

    /// <summary> For flows and tests that don't cache precompile results. </summary>
    public static PrecompileCaches Empty { get; } = new([], new PreBlockCachesConfig(), maxBytes: 0);

    private readonly FrozenDictionary<AddressAsKey, Partition> _partitions;

    /// <summary> Bounded by entry count and by <see cref="MaxSurvivingEntryBytes"/>, and is never cleared. </summary>
    private readonly ClockCache<Key, Result<byte[]>> _survivingCache;

    // ReSharper disable once UnusedMember.Global - used by DI
    /// <summary> Caches the results of every precompile from <paramref name="precompileProvider"/> that supports caching. </summary>
    public PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, IBlocksConfig blocksConfig, ILogManager? logManager = null)
        : this(precompileProvider, config, blocksConfig.PrecompileCacheMaxKilobytes.KiB, logManager) { }

    /// <summary> Byte-exact budget, bypassing <see cref="IBlocksConfig.PrecompileCacheMaxKilobytes"/>. </summary>
    internal PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
        : this(maxBytes > 0 ? CacheablePrecompiles(precompileProvider) : [], config, maxBytes, logManager) { }

    private PrecompileCaches(List<CacheablePrecompile> precompiles, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
    {
        // equal shares per precompile for now
        long partitionSize = precompiles.Count == 0 ? 0 : maxBytes / precompiles.Count;
        int survivingMaxEntries = precompiles.Count == 0 ? 0 : config.SurvivingPrecompileCacheMaxEntries;
        _survivingCache = new ClockCache<Key, Result<byte[]>>(survivingMaxEntries, comparer: EqualityComparer<Key>.Default);

        _partitions = precompiles.ToFrozenDictionary(
            static precompile => precompile.Address,
            precompile => new Partition(precompile.Name, partitionSize, _survivingCache));

        LogCacheBudget(precompiles.Count, partitionSize, maxBytes, logManager);
    }

    private static void LogCacheBudget(int partitionCount, long partitionSize, long maxBytes, ILogManager? logManager)
    {
        ILogger logger = (logManager ?? NullLogManager.Instance).GetClassLogger<PrecompileCaches>();

        if (partitionCount == 0)
        {
            if (logger.IsTrace) logger.Trace("Precompile result caching is disabled.");
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
    /// <param name="publishMetrics">
    /// Whether to publish the tier's metrics before it is emptied. Pass <see langword="false"/> for a reset that does
    /// not end a block, so the gauges keep reporting the last block instead of an already emptied cache.
    /// </param>
    public void ClearBlockCache(bool publishMetrics = true)
    {
        // published before clearing, so the occupancy gauges report each block's high point
        if (publishMetrics) PublishMetrics();

        foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
            partition.Value.Clear();
    }

    private static List<CacheablePrecompile> CacheablePrecompiles(IPrecompileProvider precompileProvider)
    {
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = precompileProvider.GetPrecompiles();

        List<CacheablePrecompile> cacheable = new(precompiles.Count);
        foreach (KeyValuePair<AddressAsKey, CodeInfo> precompile in precompiles)
        {
            if (precompile.Value.Precompile?.SupportsCaching == true)
                cacheable.Add(new CacheablePrecompile(precompile.Key, precompile.Value.Precompile.Name));
        }

        return cacheable;
    }

    private readonly record struct CacheablePrecompile(AddressAsKey Address, string Name);

    // Host-only; the zkEVM guest publishes no metrics, so the call compiles away.
    partial void PublishMetrics();

    /// <summary> One precompile's share of the per-block tier, bounded in bytes. </summary>
    /// <remarks>
    /// Admission stops at the limit instead of evicting: once a partition is full, the per-block tier stops
    /// helping for the rest of the block and lookups fall back to the surviving tier.
    /// </remarks>
    public sealed partial class Partition
    {
        private readonly ConcurrentDictionary<Key, Result<byte[]>> _entries;

        private readonly ClockCache<Key, Result<byte[]>> _survivingCache;

        private long _bytes;

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
            MaxBytes = maxBytes;
            _survivingCache = survivingCache;
            InitMetrics(name);
        }

        /// <summary> Looks <paramref name="key"/> up in this partition, then in the surviving tier. </summary>
        public bool TryGet(in Key key, out Result<byte[]> result)
        {
            if (_entries.TryGetValue(key, out result))
            {
                CountBlockHit();
                return true;
            }

            if (_survivingCache.TryGet(key, out result))
            {
                CountSurvivingHit();
                return true;
            }

            CountMiss();
            return false;
        }

        /// <summary> Stores <paramref name="result"/> under a data-owning copy of <paramref name="key"/> </summary>
        /// <returns> Whether data was saved to the per-block cache. </returns>
        /// <remarks> Reserves before checking, so a concurrent reservation near the limit can refuse an entry the partition had room for. </remarks>
        public bool TryAdd(in Key key, Result<byte[]> result)
        {
            long entryBytes = (long)key.DataLength + (result.Data?.Length ?? 0);
            long reservation = entryBytes + EntryOverheadBytes;

            bool tier1 = Interlocked.Add(ref _bytes, reservation) <= MaxBytes;
            if (!tier1)
            {
                Interlocked.Add(ref _bytes, -reservation);
                CountRejectedFull();
            }

            bool tier2 = entryBytes <= MaxSurvivingEntryBytes;
            if (!tier1 && !tier2) return false;

            // we need to rebuild the key with data copy as the data can be changed by VM processing
            // effective-input bounds are expected to remain the same
            Key copiedKey = key.WithCopiedData();

            if (tier1 && !_entries.TryAdd(copiedKey, result))
            {
                Interlocked.Add(ref _bytes, -reservation);
                tier1 = false;
            }

            if (tier1) CountAdmitted();

            if (tier2)
            {
                _survivingCache.Set(copiedKey, result);
            }

            return tier1;
        }

        internal void Clear()
        {
            _entries.NoLockClear();
            Volatile.Write(ref _bytes, 0);
            ResetAdmitted();
        }

        // Host-only metric hooks; the zkEVM guest implements none, so its calls compile away.
        partial void InitMetrics(string name);
        partial void CountBlockHit();
        partial void CountSurvivingHit();
        partial void CountMiss();
        partial void CountRejectedFull();
        partial void CountAdmitted();
        partial void ResetAdmitted();
    }

    /// <summary> Key combining precompile address, its effective input, and the fork it ran under. </summary>
    /// <remarks> The hash is computed once at construction: a lookup and insert probe both tiers several times, and each probe would otherwise rehash the whole input. </remarks>
    public readonly struct Key : IEquatable<Key>
    {
        public Key(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec)
            : this(address, data, spec, data.Span.FastHash() ^ address.GetHashCode() ^ RuntimeHelpers.GetHashCode(spec))
        {
        }

        private Key(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec, int hashCode)
        {
            Address = address;
            Data = data;
            Spec = spec;
            Hash = hashCode;
        }

        // Surviving tier is shared and needs a discriminator
        private Address Address { get; }
        private ReadOnlyMemory<byte> Data { get; }
        // Reference-compared; results may differ across forks, so entries never cross a fork boundary.
        private IReleaseSpec Spec { get; }
        private int Hash { get; }

        internal int DataLength => Data.Length;

        /// <summary> Creates a copy that owns its data. </summary>
        internal Key WithCopiedData() => new(Address, Data.ToArray(), Spec, Hash);

        public bool Equals(Key other) =>
            Hash == other.Hash && ReferenceEquals(Spec, other.Spec) && Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => Hash;
        public static bool operator ==(Key left, Key right) => left.Equals(right);
        public static bool operator !=(Key left, Key right) => !(left == right);
    }
}
