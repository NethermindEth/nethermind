// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public sealed partial class SnapshotBundle
{
    internal void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(accounts.Length, addresses.Length, nameof(accounts));
        using ArrayPoolListRef<Address> misses = new(0);
        using ArrayPoolListRef<int> positions = new(0);
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
                PromoteAccount(address, account);
                found = true;
                break;
            }
            if (found) continue;
            misses.Add(address);
            positions.Add(i);
        }
        if (misses.Count == 0) return;
        if (misses.Count < 8)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                Account? account = _readOnlySnapshotBundle.GetAccount(misses[i]);
                accounts[positions[i]] = account;
                PromoteAccount(misses[i], account);
            }
            return;
        }
        using ArrayPoolListRef<Account?> results = new(misses.Count, misses.Count);
        _readOnlySnapshotBundle.GetAccounts(misses.AsSpan(), results.AsSpan());
        for (int i = 0; i < misses.Count; i++)
        {
            accounts[positions[i]] = results[i];
            PromoteAccount(misses[i], results[i]);
        }
    }
}
