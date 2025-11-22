// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using NonBlocking;

namespace Nethermind.State.Flat;

// Reversed order so that its easy to add new KnownState.
// TODO: We can skip the reverse
public class SnapshotBundle(ArrayPoolList<Snapshot> knownStates, IPersistenceReader persistenceReader) : IDisposable
{
    Dictionary<Address, StorageSnapshotBundle> _loadedAccounts = new();
    Dictionary<Address, Account> _changedAccounts = new();
    public int SnapshotCount => knownStates.Count;

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (_changedAccounts.TryGetValue(address, out acc)) return true;

        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Accounts.TryGetValue(address, out var value))
            {
                acc = value.NewValue;
                return true;
            }
        }

        if (persistenceReader.TryGetAccount(address, out acc))
        {
            return true;
        }

        acc = null;
        return false;
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        for (int i = knownStates.Count - 1; i >= 0; i--)
        {
            if (knownStates[i].Storages.TryGetValue((address, index), out value)) return true;
            if (knownStates[i].Accounts.TryGetValue(address, out var accountInfo))
            {
                if (accountInfo.HasSelfDestruct)
                {
                    value = null;
                    return true;
                }
            }
        }

        return persistenceReader.TryGetSlot(address, index, out value);
    }

    public void ApplyStateChanges(Dictionary<Address, Account> changedValues)
    {
        foreach (var kv in changedValues)
        {
            _changedAccounts[kv.Key] = kv.Value;
        }
    }

    public void HintAccountRead(Address address, Account? account)
    {
        _changedAccounts[address] = account;
    }

    public Snapshot CollectAndApplyKnownState()
    {
        Dictionary<Address, AccountSnapshotInfo> accounts = new();

        foreach (var kv in _changedAccounts)
        {
            accounts[kv.Key] = new AccountSnapshotInfo(kv.Value, false);
        }

        Dictionary<(Address, UInt256), byte[]> storages = new ();
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            bool hasSelfDestruct = gatheredCacheStorage.Value.CollectAndApplyKnownState(storages);
            accounts[gatheredCacheStorage.Key].HasSelfDestruct = hasSelfDestruct;
        }

        var knownState = new Snapshot()
        {
            Accounts = accounts,
            Storages = storages,
        };

        knownStates.Add(knownState);

        _changedAccounts = new();
        _loadedAccounts.Clear();

        return knownState;
    }

    public StorageSnapshotBundle GatherStorageCache(Address address)
    {
        if (_loadedAccounts.TryGetValue(address, out var acc))
        {
            return acc;
        }

        StorageSnapshotBundle cache = new StorageSnapshotBundle(address, this);
        _loadedAccounts[address] = cache;
        return cache;
    }

    public Snapshot CompactToKnownState()
    {
        Dictionary<Address, AccountSnapshotInfo> accounts = new Dictionary<Address, AccountSnapshotInfo>();
        Dictionary<(Address, UInt256), byte[]> storages = new Dictionary<(Address, UInt256), byte[]>();

        if (knownStates.Count == 0) return new Snapshot(
            new StateId(-1, ValueKeccak.EmptyTreeHash), new StateId(-1, ValueKeccak.EmptyTreeHash),
            new Dictionary<Address, AccountSnapshotInfo>(),
            new Dictionary<(Address, UInt256), byte[]>()
        );

        if (knownStates.Count == 1) return knownStates[0];

        StateId to = knownStates[^1].To;
        StateId from = knownStates[1].From;

        for (int i = 0; i < knownStates.Count; i++)
        {
            var knownState = knownStates[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;

                // Clear
                if (knownStateAccount.Value.HasSelfDestruct)
                {
                    foreach (var kv in storages)
                    {
                        if (kv.Key.Item1 == address)
                        {
                            storages.Remove(kv.Key);
                        }
                    }
                }
            }

            foreach (var knownStateStorage in knownState.Storages)
            {
                storages[knownStateStorage.Key] = knownStateStorage.Value;
            }
        }

        return new Snapshot(
            from,
            to,
            accounts,
            storages);
    }

    public void Dispose()
    {
        foreach (var gatheredCacheStorage in _loadedAccounts)
        {
            gatheredCacheStorage.Value.Dispose();
        }
    }
}

public class StorageSnapshotBundle(Address account, SnapshotBundle bundle)
{
    Dictionary<UInt256, byte[]> _changedSlots = new();
    internal bool _hasSelfDestruct = false;

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }
        if (_changedSlots.TryGetValue(index, out value))
        {
            return true;
        }

        return bundle.TryGetSlot(account, index, out value);
    }

    public bool CollectAndApplyKnownState(Dictionary<(Address, UInt256), byte[]> storages)
    {
        foreach (var kv in _changedSlots)
        {
            storages[(account, kv.Key)] = kv.Value;
        }

        _changedSlots.Clear();
        _hasSelfDestruct = false;

        // TODO: Could be empty
        return _hasSelfDestruct;
    }

    public void Dispose()
    {
    }
}
