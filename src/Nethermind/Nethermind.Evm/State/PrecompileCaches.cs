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
public sealed class PrecompileCaches : IDisposable
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
        SampleBlock();

        foreach (KeyValuePair<AddressAsKey, Partition> partition in _partitions)
            partition.Value.Clear();
    }

    private static List<AddressAsKey> CacheableAddresses(IPrecompileProvider precompileProvider)
    {
        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = precompileProvider.GetPrecompiles();

        List<AddressAsKey> addresses = new(precompiles.Count);
        foreach (KeyValuePair<AddressAsKey, CodeInfo> precompile in precompiles)
        {
            if (precompile.Value.Precompile?.SupportsCaching == true)
                addresses.Add(precompile.Key);
        }

        return addresses;
    }

    #region TESTING

    // Benchmark instrumentation for the precompile-cache A/B (block tier vs surviving tier).
    // Testing branch only - never merge to master.

    /// <summary> Set to <c>false</c> to build an occupancy-only image: the JIT folds every counter guard away. </summary>
    private const bool TrackHits = true;

    /// <summary> Blocks buffered before the sampled rows go out in a single write. </summary>
    private const int FlushEveryBlocks = 128;

    private const int SampleFields = 3;

    // Sampling state is touched only from the block-processing thread, which runs ClearBlockCache inline
    // and serialized per block (BranchProcessor.QueueClearCaches), so it needs no synchronisation.
    private readonly long[] _samples = new long[FlushEveryBlocks * SampleFields];
    private KeyValuePair<AddressAsKey, Partition>[]? _partitionList;
    private int _blocks;

    /// <summary> Indexed view of <see cref="_partitions"/>, so the per-block loop skips the frozen-dictionary enumerator. </summary>
    private KeyValuePair<AddressAsKey, Partition>[] PartitionList
    {
        get
        {
            if (_partitionList is null)
            {
                _partitionList = new KeyValuePair<AddressAsKey, Partition>[_partitions.Count];
                ((ICollection<KeyValuePair<AddressAsKey, Partition>>)_partitions).CopyTo(_partitionList, 0);
            }

            return _partitionList;
        }
    }

    /// <summary> Records the block's peak occupancy, then flushes once a full batch has accumulated. </summary>
    /// <remarks> Called before the clear, so the partitions still hold what the block put in them. </remarks>
    private void SampleBlock()
    {
        KeyValuePair<AddressAsKey, Partition>[] partitions = PartitionList;
        if (partitions.Length == 0) return;

        long peakBytes = 0;
        int peakIndex = -1;
        for (int i = 0; i < partitions.Length; i++)
        {
            long used = partitions[i].Value.UsedBytes;
            if (used > peakBytes)
            {
                peakBytes = used;
                peakIndex = i;
            }
        }

        int slot = (_blocks % FlushEveryBlocks) * SampleFields;
        _samples[slot] = peakBytes;
        _samples[slot + 1] = peakIndex;
        _samples[slot + 2] = _survivingCache.Count;

        if (++_blocks % FlushEveryBlocks == 0) FlushSamples(FlushEveryBlocks);
    }

    /// <summary> Writes the buffered rows and the cumulative counter table in one stdout write. </summary>
    private void FlushSamples(int count)
    {
        KeyValuePair<AddressAsKey, Partition>[] partitions = PartitionList;
        if (count == 0 || partitions.Length == 0) return;

        int firstBlock = _blocks - count;
        System.Text.StringBuilder sb = new(count * 64 + partitions.Length * 128);

        for (int i = 0; i < count; i++)
        {
            int slot = i * SampleFields;
            int peakIndex = (int)_samples[slot + 1];
            sb.Append("PCACHE blk=").Append(firstBlock + i)
                .Append(" peak=").Append(_samples[slot])
                .Append(" peakOn=").Append(peakIndex < 0 ? "none" : partitions[peakIndex].Key.Value.ToString())
                .Append(" surv=").Append(_samples[slot + 2])
                .Append('\n');
        }

        sb.Append("PCACHE-TOTALS blocks=").Append(_blocks).Append(" survEntries=").Append(_survivingCache.Count);
        foreach (KeyValuePair<AddressAsKey, Partition> partition in partitions)
        {
            sb.Append("\nPCACHE-PART ").Append(partition.Key.Value)
                .Append(" max=").Append(partition.Value.MaxBytes);

            if (TrackHits)
            {
                sb.Append(" hitBlock=").Append(partition.Value.Tally(Partition.Tier.Block))
                    .Append(" hitSurv=").Append(partition.Value.Tally(Partition.Tier.Surviving))
                    .Append(" miss=").Append(partition.Value.Tally(Partition.Tier.Miss))
                    .Append(" refused=").Append(partition.Value.Tally(Partition.Tier.Refused));
            }
        }

        Console.Out.Write(sb.Append('\n').ToString());
    }

    /// <summary> Flushes the rows buffered since the last full batch, so a run's tail is not lost. </summary>
    public void Dispose() => FlushSamples(_blocks % FlushEveryBlocks);

    #endregion

    /// <summary> One precompile's share of the per-block tier, bounded in bytes. </summary>
    /// <remarks>
    /// Admission stops at the limit instead of evicting: once a partition is full, the per-block tier stops
    /// helping for the rest of the block and lookups fall back to the surviving tier.
    /// </remarks>
    public sealed class Partition
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

        internal Partition(long maxBytes, ClockCache<Key, Result<byte[]>> survivingCache)
        {
            // prefer partition to never resize - resizing takes locks on the whole dictionary
            int maxEntries = (int)Math.Min(maxBytes / EntryOverheadBytes, MaxPartitionCapacity);

            _entries = new ConcurrentDictionary<Key, Result<byte[]>>(CollectionExtensions.LockPartitions, maxEntries);
            MaxBytes = maxBytes;
            _survivingCache = survivingCache;
        }

        /// <summary> Looks <paramref name="key"/> up in this partition, then in the surviving tier. </summary>
        public bool TryGet(in Key key, out Result<byte[]> result)
        {
            if (_entries.TryGetValue(key, out result)) { Track(Tier.Block); return true; }
            if (_survivingCache.TryGet(key, out result)) { Track(Tier.Surviving); return true; }

            Track(Tier.Miss);
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
                Track(Tier.Refused);
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
        }

        #region TESTING

        /// <summary> Where a lookup landed, or that admission was refused by the byte budget. </summary>
        internal enum Tier { Block, Surviving, Miss, Refused }

        // Striped so the increment stays core-local: a shared counter word would cost more than the
        // cache hit it counts, and would land in proportion to the hit rate under test.
        private readonly Nethermind.Core.Threading.StripedLong[] _counters = [new(), new(), new(), new()];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Track(Tier tier)
        {
            if (TrackHits) _counters[(int)tier].Increment();
        }

        internal long Tally(Tier tier) => _counters[(int)tier].Sum;

        #endregion
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
