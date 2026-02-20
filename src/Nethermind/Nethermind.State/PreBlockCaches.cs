// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using System.Security.Principal;
using static Nethermind.State.PreBlockCaches;
using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches : IPreBlockCachesInner
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly ConcurrentDictionary<StorageCell, byte[]> _storageCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<AddressAsKey, Account> _stateCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _rlpCache = new(LockPartitions, InitialCapacity);
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public PreBlockCaches()
    {
        _clearCaches =
        [
            () => _storageCache.NoResizeClear() ? CacheType.Storage : CacheType.None,
            () => _stateCache.NoResizeClear() ? CacheType.State : CacheType.None,
            () => _precompileCache.NoResizeClear() ? CacheType.Precompile : CacheType.None
        ];
    }

    public ConcurrentDictionary<StorageCell, byte[]> StorageCache => _storageCache;
    public ConcurrentDictionary<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache => _rlpCache;
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

    public Account? GetOrAdd(AddressAsKey key, Func<AddressAsKey, Account> factory)
    {
        return _stateCache.GetOrAdd(key, factory);
    }

    public Account AddOrUpdate(AddressAsKey key, Account newValue, Func<AddressAsKey, Account, Account> updateFunc)
    {
        return _stateCache.AddOrUpdate(key, newValue, updateFunc);
    }

    public bool TryGetValue(AddressAsKey key, out Account? account)
    {
        return _stateCache.TryGetValue(key, out account);
    }

    public bool TryRemove(AddressAsKey key, out Account? account)
    {
        return _stateCache.TryRemove(key, out account);
    }

    public byte[] GetOrAdd(StorageCell key, Func<StorageCell, byte[]> factory)
    {
        return _storageCache.GetOrAdd(key, factory);
    }

    public bool TryGetValue(StorageCell key, out byte[] account)
    {
        return _storageCache.TryGetValue(key, out account);
    }

    public byte[] AddOrUpdate(StorageCell key, byte[] newValue, Func<StorageCell, byte[], byte[]> updateFunc)
    {
        return _storageCache.AddOrUpdate(key, newValue, updateFunc);
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

public interface IPreBlockCachesInner
{
    public CacheType ClearCaches();

    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache { get; }

    Account? GetOrAdd(AddressAsKey key, Func<AddressAsKey, Account> factory);
    Account AddOrUpdate(AddressAsKey key, Account newValue, Func<AddressAsKey, Account, Account> updateFunc);
    bool TryGetValue(AddressAsKey key, out Account? account);
    bool TryRemove(AddressAsKey key, out Account? account);

    byte[] GetOrAdd(StorageCell key, Func<StorageCell, byte[]> factory);
    bool TryGetValue(StorageCell key, out byte[] account);

    byte[] AddOrUpdate(StorageCell key, byte[] newValue, Func<StorageCell, byte[], byte[]> updateFunc);

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
