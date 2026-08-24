// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

    /// <summary> For flows and tests that don't cache precompile results. </summary>
    public static PrecompileCaches Empty { get; } = new([], new PreBlockCachesConfig(), maxBytes: 0);

    private readonly FrozenDictionary<AddressAsKey, Partition> _partitions;

    /// <summary> Bounded by entry count and by <see cref="MaxSurvivingEntryBytes"/>, and is never cleared. </summary>
    private readonly ClockCache<Key, Result<byte[]>> _survivingCache;

    public PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, IBlocksConfig blocksConfig, ILogManager? logManager = null)
        : this(precompileProvider, config, blocksConfig.PrecompileCacheMaxKilobytes * 1024L, logManager) { }

    /// <summary> Byte-exact budget, bypassing <see cref="IBlocksConfig.PrecompileCacheMaxKilobytes"/>. </summary>
    internal PrecompileCaches(IPrecompileProvider precompileProvider, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
        : this(maxBytes > 0 ? CacheableAddresses(precompileProvider) : [], config, maxBytes, logManager) { }

    private PrecompileCaches(List<AddressAsKey> addresses, PreBlockCachesConfig config, long maxBytes, ILogManager? logManager = null)
    {
        // equal shares per precompile for now
        long partitionSize = addresses.Count == 0 ? 0 : maxBytes / addresses.Count;
        int survivingMaxEntries = addresses.Count == 0 ? 0 : config.SurvivingPrecompileCacheMaxEntries;
        _survivingCache = new ClockCache<Key, Result<byte[]>>(survivingMaxEntries, comparer: EqualityComparer<Key>.Default);

        _partitions = addresses.ToFrozenDictionary(
            static address => address,
            _ => new Partition(partitionSize, _survivingCache));

        LogCacheBudget(addresses.Count, partitionSize, maxBytes, logManager);
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
    public int SurvivingCacheCount => _survivingCache.Count;

    /// <summary> The per-block partition for <paramref name="address"/>, or <c>false</c> if it is not cached. </summary>
    public bool TryGetPartition(Address address, [NotNullWhen(true)] out Partition? partition) =>
        _partitions.TryGetValue(address, out partition);

    /// <summary> Total per-block entries across every partition. </summary>
    public int BlockCacheCount
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
        foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
            partition.Value.Clear();
    }

    private static List<AddressAsKey> CacheableAddresses(IPrecompileProvider precompileProvider)
    {
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = precompileProvider.GetPrecompiles();
        List<AddressAsKey> addresses = new(precompiles.Count);
        addresses.AddRange(precompiles
            .Where(static precompile => precompile.Value.Precompile?.SupportsCaching == true)
            .Select(static precompile => precompile.Key));

        return addresses;
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

        private long _bytes;

        internal int Count => _entries.Count;

        public long MaxBytes { get; }

        /// <summary> Bytes reserved for this partition. </summary>
        /// <remarks>
        /// Admission reserves before it checks, so this may read above <see cref="MaxBytes"/> while an over-the-limit entry is being processed.
        /// </remarks>
        public long UsedBytes => Volatile.Read(ref _bytes);

        internal Partition(long maxBytes, ClockCache<Key, Result<byte[]>> survivingCache)
        {
            // prefer partition to never resize - resizing takes locks on the whole dictionary
            int maxEntries = (int)Math.Min(maxBytes / EntryOverheadBytes, MaxPartitionCapacity);

            _entries = new ConcurrentDictionary<Key, Result<byte[]>>(CollectionExtensions.LockPartitions, maxEntries);
            MaxBytes = maxBytes;
            _survivingCache = survivingCache;
        }

        public bool TryGet(in Key key, out Result<byte[]> result) =>
            _entries.TryGetValue(key, out result) || _survivingCache.TryGet(key, out result);

        /// <summary> Stores <paramref name="result"/> under a data-owning copy of <paramref name="key"/>, if the partition has room for it. </summary>
        public bool TryAdd(in Key key, Result<byte[]> result)
        {
            long entryBytes = (long)key.DataLength + (result.Data?.Length ?? 0);
            long reservation = entryBytes + EntryOverheadBytes;

            if (Interlocked.Add(ref _bytes, reservation) > MaxBytes)
            {
                Interlocked.Add(ref _bytes, -reservation);
                return false;
            }

            // we need to rebuild the key with data copy as the data can be changed by VM processing
            // effective-input bounds are expected to remain the same
            Key copiedKey = key.WithCopiedData();
            if (!_entries.TryAdd(copiedKey, result))
            {
                Interlocked.Add(ref _bytes, -reservation);
                return false;
            }

            if (entryBytes <= MaxSurvivingEntryBytes) _survivingCache.Set(copiedKey, result);
            return true;
        }

        internal void Clear()
        {
            _entries.NoLockClear();
            Volatile.Write(ref _bytes, 0);
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
