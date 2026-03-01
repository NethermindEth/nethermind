// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Pure value-type snapshot of journal positions (5 ints). No GC references.
/// Each EVM call frame holds one of these to enable rollback on revert.
/// The actual tracking data lives in <see cref="AccessTrackingState"/>, which is
/// transaction-scoped and held on <see cref="VirtualMachine{TGasPolicy}"/>.
/// </summary>
public struct AccessSnapshot
{
    private int _addressesSnapshots;
    private int _storageKeysSnapshots;
    private int _destroyListSnapshots;
    private int _logsSnapshots;
    private int _largeContractList;

    public void TakeSnapshot(AccessTrackingState state)
    {
        _addressesSnapshots = state.AccessedAddresses.TakeSnapshot();
        _storageKeysSnapshots = state.AccessedStorageCells.TakeSnapshot();
        _destroyListSnapshots = state.DestroyList.TakeSnapshot();
        _logsSnapshots = state.Logs.TakeSnapshot();
        _largeContractList = state.LargeContractList.TakeSnapshot();
    }

    public readonly void Restore(AccessTrackingState state)
    {
        state.AccessedAddresses.Restore(_addressesSnapshots);
        state.AccessedStorageCells.Restore(_storageKeysSnapshots);
        state.DestroyList.Restore(_destroyListSnapshots);
        state.Logs.Restore(_logsSnapshots);
        state.LargeContractList.Restore(_largeContractList);
    }
}

/// <summary>
/// Transaction-scoped mutable state for EIP-2929 warm/cold access tracking,
/// self-destruct lists, logs, and large contract tracking.
/// Pooled for reuse across transactions.
/// </summary>
public sealed class AccessTrackingState
{
    private static readonly ConcurrentQueue<AccessTrackingState> _trackerPool = new();
    public static AccessTrackingState RentState() => _trackerPool.TryDequeue(out AccessTrackingState? tracker) ? tracker : new AccessTrackingState();

    public static void ResetAndReturn(AccessTrackingState state)
    {
        state.Clear();
        _trackerPool.Enqueue(state);
    }

    public JournalSet<Address> AccessedAddresses { get; } = new(Address.EqualityComparer);
    public JournalSet<StorageCell> AccessedStorageCells { get; } = new(StorageCell.EqualityComparer);
    public JournalCollection<LogEntry> Logs { get; } = new();
    public JournalSet<Address> DestroyList { get; } = new(Address.EqualityComparer);
    public HashSet<AddressAsKey> CreateList { get; } = new(AddressAsKey.EqualityComparer);
    public JournalSet<AddressAsKey> LargeContractList { get; } = new(AddressAsKey.EqualityComparer);

    public bool IsCold(Address? address) => !AccessedAddresses.Contains(address);

    public bool IsCold(in StorageCell storageCell) => !AccessedStorageCells.Contains(storageCell);

    public bool WarmUp(Address address)
        => AccessedAddresses.Add(address);

    public bool WarmUp(in StorageCell storageCell)
        => AccessedStorageCells.Add(storageCell);

    public bool WarmUpLargeContract(Address address)
        => LargeContractList.Add(address);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void WarmUp(AccessList? accessList)
    {
        if (accessList?.IsEmpty == false)
        {
            foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
            {
                AccessedAddresses.Add(address);
                foreach (UInt256 storage in storages)
                {
                    AccessedStorageCells.Add(new StorageCell(address, in storage));
                }
            }
        }
    }

    public void ToBeDestroyed(Address address)
    {
        DestroyList.Add(address);
    }

    public void WasCreated(Address address)
    {
        CreateList.Add(address);
    }

    private void Clear()
    {
        AccessedAddresses.Clear();
        AccessedStorageCells.Clear();
        Logs.Clear();
        DestroyList.Clear();
        CreateList.Clear();
        LargeContractList.Clear();
    }
}
