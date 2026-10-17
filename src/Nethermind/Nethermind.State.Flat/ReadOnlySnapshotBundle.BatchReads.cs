// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed partial class ReadOnlySnapshotBundle
{

    internal void GetSlots(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests, Span<UInt256?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        if (recordDetailedMetrics)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[i];
                GetSlot(request.Address, in request.Slot, request.SelfDestructIdx, out values[i]);
            }
            return;
        }

        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[i];
            if (TryGetSlotFromSnapshots(request.Address, in request.Slot, request.SelfDestructIdx, out UInt256? value))
                values[i] = value;
            else
                misses.Add(i);
        }
        if (misses.Count < BatchPersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[misses[i]];
                UInt256 value = default;
                values[misses[i]] = persistenceReader.TryGetSlot(request.Address, in request.Slot, ref value) ? value : null;
            }
            return;
        }

        using ArrayPoolListRef<StorageCell> inputs = new(misses.Count, misses.Count);
        for (int i = 0; i < misses.Count; i++)
        {
            ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[misses[i]];
            inputs[i] = new StorageCell(request.Address, in request.Slot);
        }
        if (misses.Count == requests.Length)
        {
            persistenceReader.GetSlots(inputs.AsSpan(), values);
            return;
        }
        using ArrayPoolSpan<UInt256?> results = new(misses.Count);
        persistenceReader.GetSlots(inputs.AsSpan(), results);
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }

    internal bool TryGetCachedSlot(Address address, in UInt256 slot, int selfDestructIdx, out UInt256? value)
    {
        GuardDispose();
        if (TryGetSlotFromSnapshots(address, in slot, selfDestructIdx, out value,
                recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0)) return true;
        long start = recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        if (persistenceReader is not IBatchPersistenceReader batch || !batch.TryGetCachedSlot(address, in slot, out value)) return false;
        if (recordDetailedMetrics)
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start,
                value.GetValueOrDefault().IsZero ? _readStoragePersistenceNullLabel : _readStoragePersistenceLabel);
        return true;
    }

    private bool TryGetSlotFromSnapshots(Address address, in UInt256 slot, int selfDestructIdx, out UInt256? value, long start = 0)
    {
        HashedKey<(Address, UInt256)> key = new((address, slot));
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out value))
            {
                if (recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, _readStorageSnapshotLabel);
                return true;
            }
            if (_persistedSnapshotCount + i <= selfDestructIdx)
            {
                value = null;
                return true;
            }
        }
        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in slot, selfDestructIdx, start, out value)) return true;
        value = null;
        return false;
    }
}
