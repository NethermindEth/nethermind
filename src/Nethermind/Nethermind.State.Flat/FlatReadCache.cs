// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

internal sealed class FlatReadCache
{
    private const int AccountCapacity = 65_536;
    private const int StorageCapacity = 262_144;

    private readonly AssociativeCache<AddressAsKey, AccountCacheItem> _accounts = new(AccountCapacity);
    private readonly AssociativeCache<StorageCell, StorageCacheItem> _storage = new(StorageCapacity);
    private readonly ConcurrentDictionary<AddressAsKey, int> _storageGenerations = new();

    public bool TryGetAccount(Address address, out Account? account)
    {
        AddressAsKey key = address;
        if (_accounts.TryGet(in key, out AccountCacheItem? item) && item is not null)
        {
            account = item.Value;
            return true;
        }

        account = null;
        return false;
    }

    public void SetAccount(Address address, Account? account)
    {
        AddressAsKey key = address;
        _accounts.Set(in key, new AccountCacheItem(account));
    }

    public bool TryGetStorage(Address address, in UInt256 index, out byte[]? value)
    {
        StorageCell key = new(address, in index);
        if (_storage.TryGet(in key, out StorageCacheItem? item)
            && item is not null
            && item.Generation == GetStorageGeneration(address))
        {
            value = item.Value;
            return true;
        }

        value = null;
        return false;
    }

    public void SetStorage(Address address, in UInt256 index, byte[]? value)
    {
        StorageCell key = new(address, in index);
        _storage.Set(in key, new StorageCacheItem(GetStorageGeneration(address), value));
    }

    public void Invalidate(Snapshot snapshot)
    {
        foreach (KeyValuePair<HashedKey<Address>, Account?> account in snapshot.Accounts)
        {
            AddressAsKey key = account.Key.Key;
            _accounts.Delete(in key);
        }

        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> storage in snapshot.Storages)
        {
            (Address address, UInt256 index) = storage.Key.Key;
            StorageCell key = new(address, in index);
            _storage.Delete(in key);
        }

        foreach (KeyValuePair<HashedKey<Address>, bool> selfDestructedStorage in snapshot.SelfDestructedStorageAddresses)
        {
            AddressAsKey key = selfDestructedStorage.Key.Key;
            _storageGenerations.AddOrUpdate(key, 1, static (_, generation) => unchecked(generation + 1));
        }
    }

    public void Clear()
    {
        _accounts.Clear();
        _storage.Clear();
        _storageGenerations.Clear();
    }

    private int GetStorageGeneration(Address address)
    {
        AddressAsKey key = address;
        return _storageGenerations.TryGetValue(key, out int generation) ? generation : 0;
    }

    private sealed class AccountCacheItem(Account? value)
    {
        public Account? Value { get; } = value;
    }

    private sealed class StorageCacheItem(int generation, byte[]? value)
    {
        public int Generation { get; } = generation;
        public byte[]? Value { get; } = value;
    }
}
