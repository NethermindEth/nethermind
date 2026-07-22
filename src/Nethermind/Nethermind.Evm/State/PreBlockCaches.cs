// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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

    [ThreadStatic]
    private static StorageReadCapture? _currentStorageReadCapture;

    private int _activeStorageReadCaptures;

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

    /// <summary>
    /// Starts a thread-local capture of backing-store storage misses made through this block cache.
    /// </summary>
    /// <param name="skipBackingReads">
    /// When <see langword="true"/>, callers record the storage cell and use a speculative placeholder instead of
    /// reading the backing store. The speculative execution result must not be consumed.
    /// </param>
    public StorageReadCapture BeginStorageReadCapture(bool skipBackingReads)
    {
        StorageReadCapture capture = new(this, _currentStorageReadCapture, skipBackingReads);
        _currentStorageReadCapture = capture;
        Interlocked.Increment(ref _activeStorageReadCaptures);
        return capture;
    }

    /// <summary>
    /// Records a backing-store storage miss in the capture active on the current thread.
    /// </summary>
    /// <returns><see langword="true"/> when the backing read should be skipped.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CaptureStorageMiss(in StorageCell storageCell)
    {
        if (Volatile.Read(ref _activeStorageReadCaptures) == 0) return false;

        StorageReadCapture? capture = _currentStorageReadCapture;
        if (capture is null || !ReferenceEquals(capture.Owner, this)) return false;

        capture.Record(in storageCell);
        return capture.SkipBackingReads;
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

    /// <summary>
    /// A synchronous, thread-local storage-read capture. Dispose it on the thread where it was created.
    /// </summary>
    public sealed class StorageReadCapture : IDisposable
    {
        private readonly StorageReadCapture? _previous;
        private readonly HashSet<StorageCell>? _cells;
        private bool _disposed;

        internal StorageReadCapture(PreBlockCaches owner, StorageReadCapture? previous, bool skipBackingReads)
        {
            Owner = owner;
            _previous = previous;
            SkipBackingReads = skipBackingReads;
            if (skipBackingReads) _cells = [];
        }

        internal PreBlockCaches Owner { get; }
        internal bool SkipBackingReads { get; }

        /// <summary>Number of backing-store misses observed, including repeated cells.</summary>
        public int MissCount { get; private set; }

        /// <summary>Distinct cells encountered while backing reads were skipped.</summary>
        public IReadOnlyCollection<StorageCell> Cells => _cells ?? (IReadOnlyCollection<StorageCell>)Array.Empty<StorageCell>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Record(in StorageCell storageCell)
        {
            MissCount++;
            _cells?.Add(storageCell);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (ReferenceEquals(_currentStorageReadCapture, this))
            {
                _currentStorageReadCapture = _previous;
            }

            Interlocked.Decrement(ref Owner._activeStorageReadCaptures);
        }
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
    // 2^17 × 2 ways = 262144 entries, above the ~140K-slot working set at 300M gas.
    public int StorageCacheSetsBits { get; init; } = 17;

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
