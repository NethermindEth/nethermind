// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly ConcurrentDictionary<StorageCell, byte[]> _storageCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<AddressAsKey, Account> _stateCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _rlpCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<PrecompileCacheKey, (byte[], bool)> _precompileCache = new(LockPartitions, InitialCapacity, PrecompileCacheKeyComparer.Instance);

    public PreBlockCaches()
    {
        _clearCaches =
        [
            () => _storageCache.NoResizeClear() ? CacheType.Storage : CacheType.None,
            () => _stateCache.NoResizeClear() ? CacheType.State : CacheType.None,
            () => _rlpCache.NoResizeClear() ? CacheType.Rlp : CacheType.None,
            () => _precompileCache.NoResizeClear() ? CacheType.Precompile : CacheType.None
        ];
    }

    public ConcurrentDictionary<StorageCell, byte[]> StorageCache => _storageCache;
    public ConcurrentDictionary<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, (byte[], bool)> PrecompileCache => _precompileCache;

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        return isDirty;
    }

    public readonly struct PrecompileCacheKey(Address address, byte[] data)
    {
        internal Address Address { get; } = address;
        internal byte[] Data { get; } = data;
        public bool Equals(in PrecompileCacheKey other) => Address == other.Address && Data.AsSpan().SequenceEqual(other.Data);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.AsSpan().FastHash(Address.GetHashCode());
    }

    public readonly ref struct PrecompileAltCacheKey(Address address, ReadOnlySpan<byte> data)
    {
        private Address Address { get; } = address;
        private ReadOnlySpan<byte> Data { get; } = data;
        public bool Equals(in PrecompileCacheKey other) => Address == other.Address && Data.SequenceEqual(other.Data);
        public override int GetHashCode() => Data.FastHash(Address.GetHashCode());
    }

    public class PrecompileCacheKeyComparer : IEqualityComparer<PrecompileCacheKey>, IAlternateEqualityComparer<PrecompileAltCacheKey, PrecompileCacheKey>
    {
        private PrecompileCacheKeyComparer() { }
        internal static PrecompileCacheKeyComparer Instance { get; } = new();
        public PrecompileCacheKey Create(PrecompileAltCacheKey alternate) => throw new NotSupportedException();
        public bool Equals(PrecompileCacheKey x, PrecompileCacheKey y) => x.Equals(in y);
        public bool Equals(PrecompileAltCacheKey alternate, PrecompileCacheKey other) => alternate.Equals(in other);
        public int GetHashCode([DisallowNull] PrecompileCacheKey obj) => obj.GetHashCode();
        public int GetHashCode(PrecompileAltCacheKey alternate) => alternate.GetHashCode();
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
