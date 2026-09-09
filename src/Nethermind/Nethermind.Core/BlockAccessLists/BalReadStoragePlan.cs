// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Assigns dense ordinals to a block's declared storage reads and owns its worker coverage.</summary>
/// <remarks>Dispose only after all execution workers have stopped using the plan.</remarks>
public sealed class BalReadStoragePlan : IDisposable
{
    private readonly ReadOnlyBlockAccessList _bal;
    private readonly Dictionary<AddressAsKey, (ReadOnlyAccountChanges Account, int Start)> _accounts;
    private readonly ConcurrentQueue<BalReadCoverage> _workers = new();
    private bool _disposed;

    /// <summary>The number of declared storage reads.</summary>
    public int TotalReads { get; }

    /// <summary>Optional value destination for read sets larger than the shared associative cache.</summary>
    public BalStorageValueCache? StorageValues { get; }

    /// <summary>Builds a read plan, allocating a value destination only above the supplied cache capacity.</summary>
    public BalReadStoragePlan(ReadOnlyBlockAccessList bal, int storageCacheCapacity) : this(bal)
    {
        if (TotalReads > storageCacheCapacity) StorageValues = new(TotalReads);
    }

    /// <summary>Builds an index over the validated, sorted storage reads of a suggested BAL.</summary>
    public BalReadStoragePlan(ReadOnlyBlockAccessList bal)
    {
        _bal = bal;
        _accounts = new(bal.AccountChanges.Count);
        int start = 0;
        foreach (ReadOnlyAccountChanges account in bal.AccountChanges)
        {
            _accounts.Add(account.Address, (account, start));
            start = checked(start + account.StorageReads.Length);
        }
        TotalReads = start;
    }

    /// <summary>Finds a declared read's ordinal; write slots are not in this index.</summary>
    public bool TryGetOrdinal(in StorageCell cell, out int ordinal)
    {
        if (_accounts.TryGetValue(cell.Address, out (ReadOnlyAccountChanges Account, int Start) entry))
        {
            int local = entry.Account.StorageReads.AsSpan().BinarySearch(cell.Index);
            if (local >= 0)
            {
                ordinal = entry.Start + local;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    /// <summary>Creates coverage owned by this plan for one execution worker.</summary>
    public BalReadCoverage CreateCoverage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BalReadCoverage coverage = new(this);
        _workers.Enqueue(coverage);
        return coverage;
    }

    /// <summary>Finds the first declared read no worker executed.</summary>
    /// <remarks>Call only after all workers have completed; merges coverage into the first worker.</remarks>
    public bool TryFindUncovered(out Address? address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BalReadCoverage? combined = null;
        foreach (BalReadCoverage worker in _workers)
        {
            if (combined is null) combined = worker;
            else combined.Absorb(worker);
        }
        int missing = combined?.FirstUncovered(TotalReads) ?? (TotalReads == 0 ? -1 : 0);
        int end = 0;
        foreach (ReadOnlyAccountChanges account in _bal.AccountChanges)
        {
            end += account.StorageReads.Length;
            if (missing >= 0 && missing < end)
            {
                address = account.Address;
                return true;
            }
        }
        address = null;
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StorageValues?.Dispose();
        while (_workers.TryDequeue(out BalReadCoverage? worker)) worker.Release();
        _accounts.Clear();
    }
}
