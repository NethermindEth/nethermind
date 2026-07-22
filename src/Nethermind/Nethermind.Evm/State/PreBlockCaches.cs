// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;

    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache;
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);
    private readonly ClockCache<PrecompileCacheKey, Result<byte[]>> _survivingPrecompileCache;
    private volatile IWorldStateScopeProvider.IScope? _mainScope;

    public PreBlockCaches() : this(new PreBlockCachesConfig()) { }

    public PreBlockCaches(PreBlockCachesConfig config)
    {
        _storageCache = new SeqlockCache<StorageCell, byte[]>(config.StorageCacheSetsBits);
        _survivingPrecompileCache = new ClockCache<PrecompileCacheKey, Result<byte[]>>(
            config.SurvivingPrecompileCacheMaxEntries, comparer: EqualityComparer<PrecompileCacheKey>.Default);
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCache.NoLockClear(); return CacheType.None; }
        ];
    }

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;
    public ClockCache<PrecompileCacheKey, Result<byte[]>> SurvivingPrecompileCache => _survivingPrecompileCache;

    /// <summary>
    /// The main processing scope, registered for its lifetime as the target of trie warm-up hints
    /// (<see cref="IWorldStateScopeProvider.IScope.HintWarmAccount"/>); may disappear at any time.
    /// </summary>
    public IWorldStateScopeProvider.IScope? MainScope
    {
        get => _mainScope;
        set => _mainScope = value;
    }

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        return isDirty;
    }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        // Reference-compared; results may differ across forks, so entries never cross a fork boundary.
        private IReleaseSpec Spec { get; } = spec;

        public bool Equals(PrecompileCacheKey other) =>
            ReferenceEquals(Spec, other.Spec) && Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode() ^ RuntimeHelpers.GetHashCode(Spec);
    }
}

public sealed record PreBlockCachesConfig
{
    // 2^18 × 2 ways = 524288 entries, leaving headroom for the ~140K-slot working set at 300M gas.
    public int StorageCacheSetsBits { get; init; } = 18;

    public int SurvivingPrecompileCacheMaxEntries { get; init; } = 16384;
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
