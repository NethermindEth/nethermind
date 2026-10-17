// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed partial class SnapshotBundle
{

    internal void GetSlots(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests, Span<UInt256?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[i];
            HashedKey<(Address, UInt256)> key = new((request.Address, request.Slot));
            if (TryGetSlotFromCurrentSnapshots(in key, request.SelfDestructIdx, out UInt256? value))
                values[i] = value;
            else
                misses.Add(i);
        }
        if (misses.Count < BatchPersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[misses[i]];
                if (_filterInMemorySlotReads)
                    _readOnlySnapshotBundle.GetSlotFiltered(request.SelfDestructIdx, new((request.Address, request.Slot)), out values[misses[i]]);
                else
                    _readOnlySnapshotBundle.GetSlot(request.Address, in request.Slot, request.SelfDestructIdx, out values[misses[i]]);
            }
            return;
        }

        if (misses.Count == requests.Length)
        {
            _readOnlySnapshotBundle.GetSlots(requests, values);
            return;
        }
        using ArrayPoolListRef<(Address Address, int SelfDestructIdx, UInt256 Slot)> inputs = new(misses.Count, misses.Count);
        using ArrayPoolSpan<UInt256?> results = new(misses.Count);
        for (int i = 0; i < misses.Count; i++) inputs[i] = requests[misses[i]];
        _readOnlySnapshotBundle.GetSlots(inputs.AsSpan(), results);
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }

    /// <remarks>Only for misses probed within the same HintBal task, before scope mutation is allowed.</remarks>
    internal void GetSlotsAfterCacheMiss(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests,
        Span<UInt256?> values)
    {
        GuardDispose();
        _readOnlySnapshotBundle.GetSlotsAfterCacheMiss(requests, values);
    }

    internal bool TryGetCachedSlot(Address address, in UInt256 slot, int selfDestructIdx, out UInt256? value)
    {
        GuardDispose();
        HashedKey<(Address, UInt256)> key = new((address, slot));
        return TryGetSlotFromCurrentSnapshots(in key, selfDestructIdx, out value)
            || _readOnlySnapshotBundle.TryGetCachedSlot(in key, selfDestructIdx, out value);
    }

    private bool TryGetSlotFromCurrentSnapshots(in HashedKey<(Address, UInt256)> key, int selfDestructIdx, out UInt256? value)
    {
        if (_changedSlots.TryGetValue(key, out value)) return true;
        if (selfDestructIdx == _snapshots.Count + _readOnlySnapshotBundle.SnapshotCount)
        {
            value = null;
            return true;
        }

        int boundary = selfDestructIdx - _readOnlySnapshotBundle.SnapshotCount;
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorage(key, out value)) return true;
            if (i <= boundary)
            {
                value = null;
                return true;
            }
        }
        value = null;
        return false;
    }
}
