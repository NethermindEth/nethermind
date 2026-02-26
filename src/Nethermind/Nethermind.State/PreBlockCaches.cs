// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using static Nethermind.State.PreBlockCaches;
using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches : IPreBlockCachesInner
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches()
    {
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCache.NoResizeClear(); return CacheType.None; }
        ];
    }

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

    public Account? GetOrAdd(in AddressAsKey key, InFactory<AddressAsKey, Account> factory)
    {
        return _stateCache.GetOrAdd(in key, (in asKey) => factory(asKey) );
    }

    public Account AddOrUpdate(in AddressAsKey key, Account newValue, Func<AddressAsKey, Account, Account> updateFunc)
    {
        _stateCache.Set(in key, newValue);
        return newValue;
    }

    public bool TryGetValue(AddressAsKey key, out Account? account)
    {
        return _stateCache.TryGetValue(key, out account);
    }

    public bool TryRemove(AddressAsKey key, out Account? account)
    {
        account = null;
        return false;
    }

    public byte[]? GetOrAdd(in StorageCell key, InFactory<StorageCell, byte[]?> factory)
    {
        return _storageCache.GetOrAdd(in key, (in cell) => factory(cell));
    }

    public bool TryGetValue(in StorageCell key, out byte[]? data)
    {
        return _storageCache.TryGetValue(in key, out data);
    }

    public byte[] AddOrUpdate(in StorageCell key, byte[] newValue, Func<StorageCell, byte[], byte[]> updateFunc)
    {
        _storageCache.Set(in key, newValue);
        return newValue;
    }

    public void Seal() { }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
    }
}

public interface IPreBlockCachesWrapper
{
    public IPreBlockCachesInner Active { get; }
    public IPreBlockCachesInner? Next { get; }
    public IPreBlockCachesInner CreateNext();
    public void Promote();
}

public class PreBlockCachesWrapper : IPreBlockCachesWrapper
{
    private readonly PreBlockCaches _instance = new();

    public IPreBlockCachesInner Active => _instance;
    public IPreBlockCachesInner Next => _instance;

    public IPreBlockCachesInner CreateNext()
    {
        return _instance;
    }

    public void Promote() { }
}

public delegate TValue? InFactory<TKey, out TValue>(in TKey key);

public interface IPreBlockCachesInner
{
    public CacheType ClearCaches();

    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache { get; }

    Account? GetOrAdd(in AddressAsKey key, InFactory<AddressAsKey, Account> factory);
    Account? AddOrUpdate(in AddressAsKey key, Account newValue, Func<AddressAsKey, Account?, Account?> updateFunc);
    bool TryGetValue(AddressAsKey key, out Account? account);
    bool TryRemove(AddressAsKey key, out Account? account);

    byte[]? GetOrAdd(in StorageCell key, InFactory<StorageCell, byte[]> factory);
    bool TryGetValue(in StorageCell key, out byte[] account);

    byte[] AddOrUpdate(in StorageCell key, byte[] newValue, Func<StorageCell, byte[], byte[]> updateFunc);

    public void Seal();
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
