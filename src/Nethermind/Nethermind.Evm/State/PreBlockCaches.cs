// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;

    /// <summary>
    /// Set-index bits for the per-block storage cache: 2^(17+1) = 262144 entries.
    /// Sized above the distinct-slot working set of storage-heavy blocks (a 100M-gas block of cold
    /// SLOADs touches ~47K slots; ~140K at 300M gas). With the default 32K entries such blocks evict
    /// prewarmed values before the main loop reads them, turning would-be cache hits back into DB reads.
    /// </summary>
    private const int StorageCacheSetsBits = 17;

    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new(StorageCacheSetsBits);
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

[Flags]
public enum CacheType
{
    None = 0,
    Storage = 0b1,
    State = 0b10,
    Rlp = 0b100,
    Precompile = 0b1000
}
