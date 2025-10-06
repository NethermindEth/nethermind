// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NonBlocking;

namespace Nethermind.State.FlatCache;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle(ArrayPoolList<Snapshot> knownStates, IBigCache bigCache) : IDisposable
{
    Dictionary<Address, StorageSnapshotBundle> _loadedAccounts = new();
    Dictionary<Address, Account> _changedAccounts = new();
    ArrayPoolList<Address> _writtenAccounts = new(1);
    public int SnapshotCount => knownStates.Count;

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (_changedAccounts.TryGetValue(address, out acc)) return true;

        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Accounts.TryGetValue(address, out var value))
            {
                acc = value;
                return true;
            }
        }

        if (bigCache.TryGetValue(address, out acc))
        {
            return true;
        }

        acc = null;
        return false;
    }

    public void ApplyStateChanges(Dictionary<Address, Account> changedValues)
    {
        foreach (var kv in changedValues)
        {
            _writtenAccounts.Add(kv.Key);
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public void HintAccountRead(Address address, Account? account)
    {
        _changedAccounts[address] = account;
    }

    public Snapshot CollectAndApplyKnownState()
    {
        Dictionary<Address, StorageWrites> storages = new();
        ArrayPoolList<(Address, UInt256)> writes = new(1);

        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            (StorageWrites storageChange, ArrayPoolList<(Address, UInt256)> writeArr) = gatheredCacheStorage.Value.CollectAndApplyKnownState();
            storages[gatheredCacheStorage.Key] = storageChange;
            writes.AddRange(writeArr);
        }

        var knownState = new Snapshot()
        {
            Accounts = _changedAccounts,
            Storages = storages,
            AccountWrites = _writtenAccounts.ToHashSet(),
            SlotWrites = writes.ToHashSet()
        };
        _changedAccounts = new();
        _writtenAccounts.Clear();
        knownStates.Add(knownState);

        return knownState;
    }

    public StorageSnapshotBundle GatherStorageCache(Address address)
    {
        if (_loadedAccounts.TryGetValue(address, out var acc))
        {
            return acc;
        }

        ArrayPoolList<StorageWrites> accounts = new(knownStates.Count);
        for (int i = 0; i < knownStates.Count; i++)
        {
            if (knownStates[i].Storages.TryGetValue(address, out var value))
            {
                accounts.Add(value);
            }
        }

        IBigCache.IStorageReader bigCacheStorage = bigCache.GetStorageReader(address);

        StorageSnapshotBundle cache = new StorageSnapshotBundle(accounts, bigCacheStorage, address);
        _loadedAccounts[address] = cache;
        return cache;
    }

    public Snapshot CompactToKnownState()
    {
        Dictionary<Address, Account> accounts = new Dictionary<Address, Account>();
        Dictionary<Address, StorageWrites> storages = new Dictionary<Address, StorageWrites>();

        if (knownStates.Count == 0) return new Snapshot(
            new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
            new Dictionary<Address, Account>(),
            new Dictionary<Address, StorageWrites>(),
            new HashSet<Address>(),
            new HashSet<(Address, UInt256)>()
        );

        if (knownStates.Count == 1) return knownStates[0];

        StateId to = knownStates[^1].To;
        StateId from = knownStates[1].From;

        for (int i = 1; i < knownStates.Count; i++) // Note: Dont include the first state
        {
            var knownState = knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                accounts[knownStateAccount.Key] = knownStateAccount.Value;
            }

            foreach (var knownStateStorage in knownState.Storages)
            {
                StorageWrites currentStorageWrites;
                if (knownStateStorage.Value.HasSelfDestruct)
                {
                    currentStorageWrites = new StorageWrites(Slots: new Dictionary<UInt256, byte[]>(), HasSelfDestruct: true);
                    storages[knownStateStorage.Key] = currentStorageWrites;
                }
                else if (!storages.TryGetValue(knownStateStorage.Key, out currentStorageWrites))
                {
                    currentStorageWrites = new StorageWrites(Slots: new Dictionary<UInt256, byte[]>(), HasSelfDestruct: false);
                    storages[knownStateStorage.Key] = currentStorageWrites;
                }

                foreach (var kv in knownStateStorage.Value.Slots)
                {
                    currentStorageWrites.Slots[kv.Key] = kv.Value;
                }
            }
        }

        return new Snapshot(from, to, accounts, storages,
            new HashSet<Address>(),
            new HashSet<(Address, UInt256)>());
    }

    public void Dispose()
    {
        // knownStates.Dispose();
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
    }
}
