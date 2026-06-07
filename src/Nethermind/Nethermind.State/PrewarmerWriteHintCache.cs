// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public sealed class PrewarmerWriteHintCache
{
    private static readonly object Marker = new();
    private const int InitialSeedCapacity = 4096;

    private readonly SeqlockCache<StorageCell, object> _storageWrites = new();
    private readonly ConcurrentDictionary<AddressAsKey, AccountSeed> _stateSeeds = new(Nethermind.Core.Collections.CollectionExtensions.LockPartitions, InitialSeedCapacity);
    private readonly ConcurrentDictionary<StorageCell, byte[]> _storageSeeds = new(Nethermind.Core.Collections.CollectionExtensions.LockPartitions, InitialSeedCapacity);
    private int _hasStorageWrites;
    private int _hasStorageClear;

    public bool HasStorageWrites => Volatile.Read(ref _hasStorageWrites) != 0;

    public void AddStorageWrite(Address address, in UInt256 index)
    {
        StorageCell storageCell = new(address, in index);
        _storageWrites.Set(in storageCell, Marker);
        Volatile.Write(ref _hasStorageWrites, 1);
    }

    public bool MightWrite(Address address, in UInt256 index)
    {
        StorageCell storageCell = new(address, in index);
        return _storageWrites.TryGetValue(in storageCell, out _);
    }

    public void AddStateSeed(Address address, Account? account)
    {
        AddressAsKey key = address;
        _stateSeeds[key] = new(account);
    }

    public void AddStorageSeed(Address address, in UInt256 index, byte[] value)
    {
        StorageCell storageCell = new(address, in index);
        _storageSeeds[storageCell] = value;
    }

    public void AddStorageClear() => Volatile.Write(ref _hasStorageClear, 1);

    public void ApplySeeds(PreBlockCaches preBlockCaches)
    {
        if (Volatile.Read(ref _hasStorageClear) != 0)
        {
            preBlockCaches.StorageCache.Clear();
        }

        foreach (KeyValuePair<AddressAsKey, AccountSeed> seed in _stateSeeds)
        {
            AddressAsKey key = seed.Key;
            preBlockCaches.StateCache.Set(in key, seed.Value.Account);
        }

        foreach (KeyValuePair<StorageCell, byte[]> seed in _storageSeeds)
        {
            StorageCell key = seed.Key;
            preBlockCaches.StorageCache.Set(in key, seed.Value);
        }
    }

    public void ClearWarmupHints()
    {
        Volatile.Write(ref _hasStorageWrites, 0);
        _storageWrites.Clear();
    }

    public void Clear()
    {
        ClearWarmupHints();
        Volatile.Write(ref _hasStorageClear, 0);
        _stateSeeds.NoResizeClear();
        _storageSeeds.NoResizeClear();
    }

    private readonly struct AccountSeed(Account? account)
    {
        public Account? Account { get; } = account;
    }
}
