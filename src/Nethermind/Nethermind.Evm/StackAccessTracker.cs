// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct StackAccessTracker : IDisposable
{
    public readonly JournalSet<Address> AccessedAddresses => _trackingState.AccessedAddresses;
    public readonly JournalSet<StorageCell> AccessedStorageCells => _trackingState.AccessedStorageCells;
    public readonly JournalCollection<LogEntry> Logs => _trackingState.Logs;
    public readonly JournalSet<Address> DestroyList => _trackingState.DestroyList;
    public readonly HashSet<AddressAsKey> CreateList => _trackingState.CreateList;

    private TrackingState _trackingState;

    private int _addressesSnapshots;
    private int _storageKeysSnapshots;
    private int _destroyListSnapshots;
    private int _logsSnapshots;

    public StackAccessTracker(in StackAccessTracker accessTracker)
    {
        _trackingState = accessTracker._trackingState;
    }

    public StackAccessTracker()
    {
        _trackingState = TrackingState.RentState();
    }
    public readonly bool IsCold(Address? address) => !_trackingState.AccessedAddresses.Contains(address);

    public readonly bool IsCold(in StorageCell storageCell) => !_trackingState.AccessedStorageCells.Contains(storageCell);

    public readonly void WarmUp(Address address)
    {
        _trackingState.AccessedAddresses.Add(address);
    }

    public readonly void WarmUp(in StorageCell storageCell)
    {
        _trackingState.AccessedStorageCells.Add(storageCell);
    }

    public readonly void WarmUp(AccessList? accessList)
    {
        if (accessList?.IsEmpty == false)
        {
            foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
            {
                _trackingState.AccessedAddresses.Add(address);
                foreach (UInt256 storage in storages)
                {
                    _trackingState.AccessedStorageCells.Add(new StorageCell(address, in storage));
                }
            }
        }
    }

    public readonly void ToBeDestroyed(Address address)
    {
        _trackingState.DestroyList.Add(address);
    }

    public readonly void WasCreated(Address address)
    {
        _trackingState.CreateList.Add(address);
    }

    public void TakeSnapshot()
    {
        _addressesSnapshots = _trackingState.AccessedAddresses.TakeSnapshot();
        _storageKeysSnapshots = _trackingState.AccessedStorageCells.TakeSnapshot();
        _destroyListSnapshots = _trackingState.DestroyList.TakeSnapshot();
        _logsSnapshots = _trackingState.Logs.TakeSnapshot();
    }

    public readonly void Restore()
    {
        _trackingState.Logs.Restore(_logsSnapshots);
        _trackingState.DestroyList.Restore(_destroyListSnapshots);
        _trackingState.AccessedAddresses.Restore(_addressesSnapshots);
        _trackingState.AccessedStorageCells.Restore(_storageKeysSnapshots);
    }

    public void Dispose()
    {
        TrackingState state = _trackingState;
        _trackingState = null;
        TrackingState.ResetAndReturn(state);
    }

    private sealed class TrackingState
    {
        private static readonly ConcurrentQueue<TrackingState> _trackerPool = new();
        public static TrackingState RentState() => _trackerPool.TryDequeue(out TrackingState tracker) ? tracker : new TrackingState();

        public static void ResetAndReturn(TrackingState state)
        {
            state.Clear();
            _trackerPool.Enqueue(state);
        }

        public JournalSet<Address> AccessedAddresses { get; } = new();
        public JournalSet<StorageCell> AccessedStorageCells { get; } = new();
        public JournalCollection<LogEntry> Logs { get; } = new();
        public JournalSet<Address> DestroyList { get; } = new();
        public HashSet<AddressAsKey> CreateList { get; } = new();

        private void Clear()
        {
            AccessedAddresses.Clear();
            AccessedStorageCells.Clear();
            Logs.Clear();
            DestroyList.Clear();
            CreateList.Clear();
        }
    }
}
