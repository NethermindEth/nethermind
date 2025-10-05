// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Prometheus;

namespace Nethermind.State.FlatCache;

public sealed class BigCache: IBigCache
{
    private ConcurrentDictionary<Address, (long, Account)> Accounts = new ConcurrentDictionary<Address, (long, Account)>();
    internal ConcurrentDictionary<Address, long> SelfDestructBlockNum = new ConcurrentDictionary<Address, long>();

    private ConcurrentDictionary<StorageCell, (long, byte[])> Storages = new ConcurrentDictionary<StorageCell, (long, byte[])>();

    private Snapshot? _copyingSnapshot;

    private long _currentBlockNumber = -1;
    public long CurrentBlockNumber => _currentBlockNumber;

    private int _snapshotCount = 0;
    public long SnapshotCount => _snapshotCount;

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

    public IBigCache.IStorageReader GetStorageReader(Address address)
    {
        return new BigCacheStorageReader(address, this);
    }

    private Gauge _snapshotCountMetric = Metrics.CreateGauge("flatcache_bigcache_snapshot_count", "snapshot count");
    private Gauge _estimatedSlot = Metrics.CreateGauge("flatcache_bigcache_slot_count", "snapshot count");
    private Gauge _estimatedAccount = Metrics.CreateGauge("flatcache_bigcache_account_count", "snapshot count");

#pragma warning disable CS9113 // Parameter is unread.
    public sealed class BigCacheStorageReader(Address address, BigCache theCacheItself): IBigCache.IStorageReader
#pragma warning restore CS9113 // Parameter is unread.
    {
        [MethodImpl(MethodImplOptions.NoOptimization)]
        public bool TryGetValue(in UInt256 slot, out byte[]? value)
        {
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

            if (!theCacheItself.SelfDestructBlockNum.TryGetValue(address, out long selfDestructTime))
            {
                selfDestructTime = 0;
            }

            StorageCell key = new StorageCell(address, slot);
            if (theCacheItself.Storages.TryGetValue(key, out (long, byte[]) entry) && entry.Item1 >= selfDestructTime)
            {
                value = entry.Item2;
                return true;
            }
            value = null;
            return false;
        }
    }

    public ValueHash256? CurrentStateRoot = null;

    public void Add(StateId pickedSnapshot, Snapshot knownState)
    {
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
            if (knownStateStorage.Value.HasSelfDestruct)
            {
                // Self destruct first. Concurrent reader will just drop existing entries with block number lower than selfdestruct
                SelfDestructBlockNum.TryAdd(knownStateStorage.Key, blockNumber);
            }

            Address addr = knownStateStorage.Key;
            foreach (var kv in knownStateStorage.Value.Slots)
            {
                StorageCell key = new StorageCell(addr, kv.Key);
                if (!Storages.ContainsKey(key))
                {
                    _estimatedSlot.Inc();
                }
                Storages[key] = (blockNumber, kv.Value);
            }
        }

        _currentBlockNumber = pickedSnapshot.blockNumber;
        CurrentStateRoot = pickedSnapshot.stateRoot;
        _snapshotCount++;
        _snapshotCountMetric.Set(SnapshotCount);
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
            Address addr = knownStateStorage.Key;
            foreach (var kv in knownStateStorage.Value.Slots)
            {
                StorageCell key = new StorageCell(addr, kv.Key);

                if (Storages.TryGetValue(key, out var entry) && entry.Item1 <= blockNumber)
                {
                    Storages.Remove(key, out var _);
                    _estimatedSlot.Dec();
                }

            }
        }

        _snapshotCount--;
    }
}
