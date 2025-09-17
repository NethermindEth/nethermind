// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Int256;
using Prometheus;

namespace Nethermind.State.FlatCache;

public class BigCache
{
    private ConcurrentDictionary<Address, (long, Account)> Accounts = new ConcurrentDictionary<Address, (long, Account)>();
    private ConcurrentDictionary<Address, ConcurrentDictionary<UInt256, (long, byte[])>> Storages = new ConcurrentDictionary<Address, ConcurrentDictionary<UInt256, (long, byte[])>>();

    private Snapshot? _copyingSnapshot;

    public bool TryGetValue(Address address, out Account? acc)
    {
        /*
        if (_copyingSnapshot?.Accounts.TryGetValue(address, out acc) ?? false)
        {
            return true;
        }
        */

        if(Accounts.TryGetValue(address, out (long, Account) entry))
        {
            acc = entry.Item2;
            return true;
        }

        acc = null;
        return false;
    }

    public BigCacheStorageReader GetStorageReader(Address address)
    {
        return new BigCacheStorageReader(address, Storages.GetOrAdd(address, (_) => new ConcurrentDictionary<UInt256, (long, byte[])>()), this);
    }

    private Gauge _snapshotCount = Metrics.CreateGauge("flatcache_bigcache_snapshot_count", "snapshot count");
    private Gauge _estimatedSlot = Metrics.CreateGauge("flatcache_bigcache_slot_count", "snapshot count");
    private Gauge _estimatedAccount = Metrics.CreateGauge("flatcache_bigcache_account_count", "snapshot count");

#pragma warning disable CS9113 // Parameter is unread.
    public class BigCacheStorageReader(Address address, ConcurrentDictionary<UInt256, (long, byte[])> bigCacheStorage, BigCache theCacheItself)
#pragma warning restore CS9113 // Parameter is unread.
    {
        [MethodImpl(MethodImplOptions.NoOptimization)]
        public bool TryGetValue(UInt256 slot, out byte[]? value)
        {
            if (bigCacheStorage is null)
            {
                Console.Error.WriteLine("BigCacheStorageReader storage is null");
            }

            /*
            Snapshot? copyingSnapshot = theCacheItself._copyingSnapshot;
            if (copyingSnapshot is { Storages: null })
            {
                Console.Error.WriteLine("Storages is null. Howw?");
            }

            if (
                copyingSnapshot is {} copying &&
                copying.Storages.TryGetValue(address, out var storageMap))
            {
                if (storageMap.Slots is null) Console.Error.WriteLine("slots is null");
                if (storageMap.Slots.TryGetValue(slot, out value))
                {
                    return true;
                }

                if (storageMap.HasSelfDestruct)
                {
                    // Dont check bigcache
                    return false;
                }
            }
            */

            if (bigCacheStorage.TryGetValue(slot, out (long, byte[]) entry))
            {
                value = entry.Item2;
                return true;
            }
            value = null;
            return false;
        }
    }

    public long CurrentBlockNumber = -1;
    public int SnapshotCount = 0;

    public void Add(StateId pickedSnapshot, Snapshot knownState)
    {
        if (knownState.Storages is null)
        {
            throw new Exception($"How in the world is the storage for {pickedSnapshot} null?");
        }
        long blockNumber = pickedSnapshot.blockNumber;
        _copyingSnapshot = knownState;

        foreach (var knownStateAccount in knownState.Accounts)
        {
            if (!Accounts.ContainsKey(knownStateAccount.Key))
            {
                _estimatedAccount.Inc();
            }
            Accounts[knownStateAccount.Key] = (blockNumber, knownStateAccount.Value);
        }

        foreach (var knownStateStorage in knownState.Storages)
        {
            ConcurrentDictionary<UInt256, (long, byte[])> currentStorageWrites;
            if (knownStateStorage.Value.HasSelfDestruct)
            {
                currentStorageWrites = Storages.AddOrUpdate(knownStateStorage.Key, (_) => new ConcurrentDictionary<UInt256, (long, byte[])>(), (_, _) => new ConcurrentDictionary<UInt256, (long, byte[])>());
            }
            else
            {
                currentStorageWrites = Storages.GetOrAdd(knownStateStorage.Key, (_) => new ConcurrentDictionary<UInt256, (long, byte[])>());
            }

            foreach (var kv in knownStateStorage.Value.Slots)
            {
                if (!currentStorageWrites.ContainsKey(kv.Key))
                {
                    _estimatedSlot.Inc();
                }
                currentStorageWrites[kv.Key] = (blockNumber, kv.Value);
            }
        }

        CurrentBlockNumber = pickedSnapshot.blockNumber;
        SnapshotCount++;
        _snapshotCount.Set(SnapshotCount);
        _copyingSnapshot = null;
    }

    public void Subtract(StateId pickedSnapshot, Snapshot knownState)
    {
        long blockNumber = pickedSnapshot.blockNumber;

        foreach (var knownStateAccount in knownState.Accounts)
        {
            if (Accounts.TryGetValue(knownStateAccount.Key, out var accountEntry) && accountEntry.Item1 == blockNumber)
            {
                if (accountEntry.Item1 < blockNumber)
                {
                    Console.Error.WriteLine($"It can e less for some reason. {accountEntry.Item1} vs {blockNumber}");
                }
                Accounts.Remove(knownStateAccount.Key, out var _);
                _estimatedAccount.Dec();
            }
        }

        foreach (var knownStateStorage in knownState.Storages)
        {
            if (Storages.TryGetValue(knownStateStorage.Key, out var storageMap))
            {
                foreach (var kv in knownStateStorage.Value.Slots)
                {
                    if (storageMap.TryGetValue(kv.Key, out var entry) && entry.Item1 == blockNumber)
                    {
                        storageMap.Remove(kv.Key, out var _);
                        _estimatedSlot.Dec();
                    }
                }

                if (storageMap.Count == 0)
                {
                    Storages.Remove(knownStateStorage.Key, out var _);
                }
            }
        }

        SnapshotCount--;
    }
}
