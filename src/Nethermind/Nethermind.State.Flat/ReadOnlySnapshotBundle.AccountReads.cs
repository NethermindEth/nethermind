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
            misses.Add(address);
            positions.Add(i);
        }
        if (misses.Count == 0) return;
        if (misses.Count < 8)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
                Account? account = persistenceReader.GetAccount(misses[i]);
                accounts[positions[i]] = account;
                if (recordDetailedMetrics)
                    Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start,
                        account is null ? _readAccountPersistenceNullLabel : _readAccountPersistenceLabel);
            }
            return;
        }
        using ArrayPoolListRef<Account?> results = new(misses.Count, misses.Count);
        long readStart = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        persistenceReader.ReadAccounts(misses.AsSpan(), results.AsSpan());
        if (recordDetailedMetrics)
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - readStart, _readAccountPersistenceBatchLabel);
        for (int i = 0; i < misses.Count; i++)
        {
            Account? account = results[i];
            accounts[positions[i]] = account;

        }
    }
}
