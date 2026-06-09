// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches() => _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCache.NoResizeClear(); return CacheType.None; }
        ];

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    // Reads beyond the associative StorageCache's 2-way capacity (16384 sets x 2) conflict-evict,
    // so a block declaring more than this many reads is served from an exact-sized ordinal
    // destination instead. Below the threshold the StorageCache suffices and none is built.
    public const int StorageReadDestinationThreshold = 32768;

    private BalReadStoragePlan? _storageReadPlan;
    private BalStorageValueCache? _storageValueDestination;
    private bool _readCoverageEnabled;
    // ConcurrentQueue, not ConcurrentBag: workers (producers) enqueue on their own threads while the
    // validator (consumer) drains on the block-processing thread. Bag is thread-local-segmented, so a
    // cross-thread drain is unreliable; the queue guarantees every enqueued coverage is dequeued.
    private readonly ConcurrentQueue<BalReadCoverage> _readCoverages = new();

    /// <summary>Dense read-ordinal model for the current block, or null when neither coverage nor a destination is active.</summary>
    public BalReadStoragePlan? StorageReadPlan => _storageReadPlan;

    /// <summary>Ordinal-keyed prefetch destination for the current block, or null when the read set is small.</summary>
    public BalStorageValueCache? StorageValueDestination => _storageValueDestination;

    /// <summary>True when verify-only per-worker read-coverage validation is active for the current block.</summary>
    public bool ReadCoverageEnabled => _readCoverageEnabled;

    /// <summary>
    /// Builds the ordinal-keyed read destination when <paramref name="bal"/> declares more reads than
    /// the associative cache can hold without conflict-evicting; otherwise leaves it null. The plan is
    /// shared with read coverage when both are active for the block.
    /// </summary>
    public void BuildStorageReadDestination(ReadOnlyBlockAccessList bal)
    {
        if (bal.TotalStorageReads <= StorageReadDestinationThreshold) return;

        EnsureReadPlan(bal);
        _storageValueDestination ??= new BalStorageValueCache(_storageReadPlan!.TotalReads);
    }

    /// <summary>
    /// Enables per-worker storage-read coverage for the current verify-only-parallel block by ensuring
    /// the read-ordinal plan exists, so workers mark coverage by ordinal instead of materializing a
    /// generated read set.
    /// </summary>
    /// <remarks>
    /// Both this and <see cref="BuildStorageReadDestination"/> are idempotent builders that share the
    /// read plan; the prior block's resources are released by <see cref="ClearCaches"/>, which the
    /// block-processing loop awaits before either builder runs (only one block is in flight at a time).
    /// </remarks>
    public void EnableReadCoverage(ReadOnlyBlockAccessList bal)
    {
        EnsureReadPlan(bal);
        _readCoverageEnabled = true;
    }

    /// <summary>Rents a per-worker coverage sized to the block's read ordinal space and registers it for the block-end reduce.</summary>
    public BalReadCoverage RentReadCoverage()
    {
        BalReadCoverage coverage = new(_storageReadPlan!.TotalReads);
        _readCoverages.Enqueue(coverage);
        return coverage;
    }

    /// <summary>
    /// OR-reduces every worker's coverage into one instance and returns it (the caller disposes it);
    /// the rest are disposed here. Returns null if no worker produced coverage this block.
    /// </summary>
    public BalReadCoverage? DrainAndReduceReadCoverage()
    {
        BalReadCoverage? reduced = null;
        while (_readCoverages.TryDequeue(out BalReadCoverage? coverage))
        {
            if (reduced is null) reduced = coverage;
            else { reduced.Absorb(coverage); coverage.Dispose(); }
        }
        return reduced;
    }

    private void EnsureReadPlan(ReadOnlyBlockAccessList bal) => _storageReadPlan ??= BalReadStoragePlan.Build(bal);

    private void ReleaseReadResources()
    {
        _storageValueDestination?.Dispose();
        _storageValueDestination = null;
        _storageReadPlan = null;
        _readCoverageEnabled = false;
        while (_readCoverages.TryDequeue(out BalReadCoverage? coverage)) coverage.Dispose();
    }

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        ReleaseReadResources();
        return isDirty;
    }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
    }
}

[Flags]
public enum CacheType
{
    None = 0,
    Storage = 0b1,
    State = 0b10,
    Rlp = 0b100,
    Precompile = 0b1000
}
