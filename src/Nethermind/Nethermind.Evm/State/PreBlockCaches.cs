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

    private BalStorageReadPlan? _storageReadPlan;
    private BalStorageValueCache? _storageValueDestination;

    /// <summary>Dense read-ordinal model for the current block, or null when the read set is small.</summary>
    public BalStorageReadPlan? StorageReadPlan => _storageReadPlan;

    /// <summary>Ordinal-keyed prefetch destination for the current block, or null when the read set is small.</summary>
    public BalStorageValueCache? StorageValueDestination => _storageValueDestination;

    /// <summary>
    /// Builds the ordinal-keyed read destination when <paramref name="bal"/> declares more reads than
    /// the associative cache can hold without conflict-evicting; otherwise leaves it null. Replaces
    /// (and releases) any prior block's destination.
    /// </summary>
    public void BuildStorageReadDestination(ReadOnlyBlockAccessList bal)
    {
        ReleaseStorageReadDestination();
        if (bal.TotalStorageReads <= StorageReadDestinationThreshold) return;

        BalStorageReadPlan plan = BalStorageReadPlan.Build(bal);
        _storageReadPlan = plan;
        _storageValueDestination = new BalStorageValueCache(plan.TotalReads);
    }

    private void ReleaseStorageReadDestination()
    {
        _storageValueDestination?.Dispose();
        _storageValueDestination = null;
        _storageReadPlan = null;
    }

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        ReleaseStorageReadDestination();
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
