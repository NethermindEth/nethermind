// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
    private int _warmthSnapshots;

    public readonly bool IsCold(Address? address) => !_trackingState.AccessedAddresses.Contains(address);

    public readonly bool IsCold(in StorageCell storageCell) => !_trackingState.AccessedStorageCells.Contains(storageCell);

    public readonly bool WarmUp(Address address)
        => _trackingState.AccessedAddresses.Add(address);

    public readonly bool WarmUp(in StorageCell storageCell)
    {
        // Verify-only declared reads carry warmth in an ordinal bitset instead of the 52-byte cell
        // journal set. The lanes are disjoint (EIP-7928 reads are read-only, changes are written), so a
        // cell lives in exactly one; non-verify execution leaves Warmth null and takes the cell path.
        BalReadWarmth? warmth = _trackingState.Warmth;
        return warmth is not null && _trackingState.Plan!.TryGetGlobalReadOrdinal(storageCell.Address, in storageCell.Index, out int ordinal)
            ? warmth.WarmUp(ordinal)
            : _trackingState.AccessedStorageCells.Add(storageCell);
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
                    // Route through WarmUp so access-list pre-warming hits the same lane as the SLOAD.
                    WarmUp(new StorageCell(address, in storage));
                }
            }
        }
    }

    /// <summary>
    /// Routes EIP-2929 warmth for the BAL's declared (read-only) storage slots through an ordinal bitset
    /// keyed by <paramref name="plan"/> instead of the storage-cell journal set. Verify-only, non-tracing
    /// path; the tracker takes ownership of <paramref name="warmth"/> and disposes it when reset.
    /// </summary>
    public readonly void EnableDeclaredReadWarmth(BalReadStoragePlan plan, BalReadWarmth warmth)
    {
        _trackingState.Plan = plan;
        _trackingState.Warmth = warmth;
    }

    public readonly void ToBeDestroyed(Address address) => _trackingState.DestroyList.Add(address);

    public readonly void WasCreated(Address address) => _trackingState.CreateList.Add(address);

    public void TakeSnapshot()
    {
        _addressesSnapshots = _trackingState.AccessedAddresses.TakeSnapshot();
        _storageKeysSnapshots = _trackingState.AccessedStorageCells.TakeSnapshot();
        _destroyListSnapshots = _trackingState.DestroyList.TakeSnapshot();
        _logsSnapshots = _trackingState.Logs.TakeSnapshot();
        _warmthSnapshots = _trackingState.Warmth?.TakeSnapshot() ?? 0;
    }

    public readonly void Restore()
    {
        // When tracing access, don't restore the access sets on sub-frame revert.
        // The generated list will pre-warm all touched addresses.
        if (!_isTracingAccess)
        {
            _trackingState.AccessedAddresses.Restore(_addressesSnapshots);
            _trackingState.AccessedStorageCells.Restore(_storageKeysSnapshots);
            _trackingState.Warmth?.Restore(_warmthSnapshots);
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
        private static readonly ConcurrentQueue<TrackingState> _trackerPool = new();
        public static TrackingState RentState() => _trackerPool.TryDequeue(out TrackingState tracker) ? tracker : new TrackingState();

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

        // Per-tx verify-only declared-read warmth; null on the normal path. Owned here (disposed on Clear)
        // so the pooled state returns its bitset arrays between transactions.
        public BalReadWarmth? Warmth { get; set; }
        public BalReadStoragePlan? Plan { get; set; }

        private void Clear()
        {
            AccessedAddresses.Clear();
            AccessedStorageCells.Clear();
            Logs.Clear();
            DestroyList.Clear();
            CreateList.Clear();
            Warmth?.Dispose();
            Warmth = null;
            Plan = null;
        }
    }
}
