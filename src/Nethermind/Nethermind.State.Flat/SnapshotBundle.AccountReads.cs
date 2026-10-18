// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public sealed partial class SnapshotBundle
{
    internal bool TryGetCachedAccount(Address address, out Account? account)
    {
        GuardDispose();
        HashedKey<Address> key = new(address);
        if (_changedAccounts.TryGetValue(key, out account) || _readAccounts.TryGetValue(key, out account)) return true;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (!_snapshots[i].TryGetAccount(key, out account)) continue;
            PromoteAccount(key, account);
            return true;
        }
        if (!_readOnlySnapshotBundle.TryGetCachedAccount(address, key, out account)) return false;
        PromoteAccount(key, account);
        return true;
    }

    internal void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(accounts.Length, addresses.Length, nameof(accounts));
        using AccountReadBatch misses = new(addresses.Length);
        for (int i = 0; i < addresses.Length; i++)
        {
            Address address = addresses[i];
            HashedKey<Address> key = new(address);
            if (_changedAccounts.TryGetValue(key, out Account? account) || _readAccounts.TryGetValue(key, out account))
            {
                accounts[i] = account;
                continue;
            }

            bool found = false;
            for (int j = _snapshots.Count - 1; j >= 0; j--)
            {
                if (!_snapshots[j].TryGetAccount(key, out account)) continue;
                accounts[i] = account;
                PromoteAccount(key, account);
                found = true;
                break;
            }
            if (found) continue;
            misses.Add(in key, i);
        }
        if (misses.Count == 0) return;
        if (misses.Count < AccountReadBatch.MinReadSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                Account? account = _readOnlySnapshotBundle.GetAccount(misses.Addresses[i], misses.Keys[i]);
                accounts[misses.Positions[i]] = account;
                PromoteAccount(misses.Keys[i], account);
            }
            return;
        }
        using ArrayPoolListRef<Account?> results = new(misses.Count, misses.Count);
        _readOnlySnapshotBundle.GetAccounts(misses.Addresses, misses.Keys, results.AsSpan());
        for (int i = 0; i < misses.Count; i++)
        {
            accounts[misses.Positions[i]] = results[i];
            PromoteAccount(misses.Keys[i], results[i]);
        }
    }
}
