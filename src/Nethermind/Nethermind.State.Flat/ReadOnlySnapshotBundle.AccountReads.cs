// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{
    private static readonly StringLabel _readAccountPersistenceBatchLabel = new("account_persistence_batch");
    internal void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> accounts) => GetAccounts(addresses, default, accounts);

    internal void GetAccounts(ReadOnlySpan<Address> addresses, ReadOnlySpan<HashedKey<Address>> keys, Span<Account?> accounts)
    {
        GuardDispose();
        if (!keys.IsEmpty) ArgumentOutOfRangeException.ThrowIfNotEqual(keys.Length, addresses.Length, nameof(keys));
        ArgumentOutOfRangeException.ThrowIfNotEqual(accounts.Length, addresses.Length, nameof(accounts));
        using AccountReadBatch misses = new(addresses.Length);
        for (int i = 0; i < addresses.Length; i++)
        {
            Address address = addresses[i];
            HashedKey<Address> key = keys.IsEmpty ? new(address) : keys[i];
            long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
            bool found = false;
            for (int j = snapshots.Count - 1; j >= 0; j--)
            {
                if (!snapshots[j].TryGetAccount(key, out Account? account)) continue;
                accounts[i] = account;
                if (recordDetailedMetrics)
                    Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readAccountSnapshotLabel);
                found = true;
                break;
            }
            if (found) continue;
            if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetAccount(address, out Account? persistedAccount))
            {
                accounts[i] = persistedAccount;
                continue;
            }
            misses.Add(address, i);
        }
        if (misses.Count == 0) return;
        if (misses.Count < AccountReadBatch.MinReadSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                accounts[misses.Positions[i]] = GetAccountFromPersistence(misses.Addresses[i]);
            }
            return;
        }
        using ArrayPoolListRef<Account?> results = new(misses.Count, misses.Count);
        long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.ReadAccounts(misses.Addresses, results.AsSpan());
        if (recordDetailedMetrics)
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart, _readAccountPersistenceBatchLabel);
        for (int i = 0; i < misses.Count; i++)
        {
            accounts[misses.Positions[i]] = results[i];
        }
    }
}
