// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;

    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache;
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches() : this(new PreBlockCachesConfig()) { }

    public PreBlockCaches(PreBlockCachesConfig config)
    {
        _storageCache = new SeqlockCache<StorageCell, byte[]>(config.StorageCacheSetsBits);
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCache.NoResizeClear(); return CacheType.None; }
        ];
    }

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

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

/// <summary>
/// Tuning knobs for the per-block warm caches owned by <see cref="PreBlockCaches"/>.
/// </summary>
public sealed record PreBlockCachesConfig
{
    /// <summary>
    /// Set-index bits for the per-block storage cache (2^bits sets × 2 ways entries total).
    /// </summary>
    /// <remarks>
    /// Default 17 → 2^17 sets × 2 ways = 262144 entries — above the distinct-slot working
    /// set of storage-heavy blocks (~140K slots at 300M gas), so prewarmed values survive
    /// until the main loop reads them. Tests override this to a smaller value to keep
    /// memory pressure low when many independent test blockchains run in the same process.
    /// </remarks>
    public int StorageCacheSetsBits { get; init; } = 17;
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
