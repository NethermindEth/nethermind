// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct StackAccessTracker(bool isTracingAccess) : IDisposable
{
    public StackAccessTracker() : this(false) { }

    public readonly JournalSet<Address> AccessedAddresses => _trackingState.AccessedAddresses;
    public readonly JournalSet<StorageCell> AccessedStorageCells => _trackingState.AccessedStorageCells;
    public readonly JournalCollection<LogEntry> Logs => _trackingState.Logs;
    public readonly JournalSet<Address> DestroyList => _trackingState.DestroyList;
    public readonly HashSet<AddressAsKey> CreateList => _trackingState.CreateList;

    private readonly bool _isTracingAccess = isTracingAccess;
    private TrackingState _trackingState = TrackingState.RentState();

    private int _addressesSnapshots;
    private int _storageKeysSnapshots;
    private int _destroyListSnapshots;
    private int _logsSnapshots;

    public readonly bool IsCold(Address? address) => !_trackingState.AccessedAddresses.Contains(address);

    public readonly bool IsCold(in StorageCell storageCell) => !_trackingState.AccessedStorageCells.Contains(storageCell);

    public readonly bool WarmUp(Address address)
    {
        TrackingState state = _trackingState;
        if (state.HasLastAddress && state.LastAddress == address)
        {
            return false;
        }

        bool added = state.AccessedAddresses.Add(address);
        state.LastAddress = address;
        state.HasLastAddress = true;
        return added;
    }

    public readonly bool WarmUp(in StorageCell storageCell)
    {
        TrackingState state = _trackingState;
        if (state.HasLastStorageCell && state.LastStorageCell.Equals(storageCell))
        {
            return false;
        }

        bool added = state.AccessedStorageCells.Add(storageCell);
        state.LastStorageCell = storageCell;
        state.HasLastStorageCell = true;
        return added;
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

    public readonly void ToBeDestroyed(Address address) => _trackingState.DestroyList.Add(address);

    public readonly void WasCreated(Address address) => _trackingState.CreateList.Add(address);

    public void TakeSnapshot()
    {
        _addressesSnapshots = _trackingState.AccessedAddresses.TakeSnapshot();
        _storageKeysSnapshots = _trackingState.AccessedStorageCells.TakeSnapshot();
        _destroyListSnapshots = _trackingState.DestroyList.TakeSnapshot();
        _logsSnapshots = _trackingState.Logs.TakeSnapshot();
    }

    public readonly void Restore()
    {
        // When tracing access, don't restore the access sets on sub-frame revert.
        // The generated list will pre-warm all touched addresses.
        if (!_isTracingAccess)
        {
            _trackingState.AccessedAddresses.Restore(_addressesSnapshots);
            _trackingState.AccessedStorageCells.Restore(_storageKeysSnapshots);
            _trackingState.ClearLastAccess();
        }
        _trackingState.DestroyList.Restore(_destroyListSnapshots);
        _trackingState.Logs.Restore(_logsSnapshots);
    }

    public void Dispose()
    {
        TrackingState state = _trackingState;
        _trackingState = null;
        TrackingState.ResetAndReturn(state);
    }

    private sealed class TrackingState
    {
        private static readonly
#if ZK_EVM
            ZkEvmQueue<TrackingState>
#else
            System.Collections.Concurrent.ConcurrentQueue<TrackingState>
#endif
            _trackerPool = new();

        public static TrackingState RentState()
        {
            if (_trackerPool.TryDequeue(out TrackingState tracker)) return tracker;
            return new TrackingState();
        }

        public static void ResetAndReturn(TrackingState state)
        {
            state.Clear();
            _trackerPool.Enqueue(state);
        }

        public JournalSet<Address> AccessedAddresses { get; } = new(Address.EqualityComparer);
        public JournalSet<StorageCell> AccessedStorageCells { get; } = new(StorageCell.EqualityComparer);
        public JournalCollection<LogEntry> Logs { get; } = [];
        public JournalSet<Address> DestroyList { get; } = new(Address.EqualityComparer);
        public HashSet<AddressAsKey> CreateList { get; } = new(AddressAsKey.EqualityComparer);
        public Address? LastAddress;
        public bool HasLastAddress;
        public StorageCell LastStorageCell;
        public bool HasLastStorageCell;

        public void ClearLastAccess()
        {
            LastAddress = null;
            HasLastAddress = false;
            LastStorageCell = default;
            HasLastStorageCell = false;
        }

        private void Clear()
        {
            AccessedAddresses.Clear();
            AccessedStorageCells.Clear();
            Logs.Clear();
            DestroyList.Clear();
            CreateList.Clear();
            ClearLastAccess();
        }
    }
}
