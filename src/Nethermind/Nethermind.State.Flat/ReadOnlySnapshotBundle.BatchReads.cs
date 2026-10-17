// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private bool TryGetSlotFromSnapshots(Address address, in UInt256 slot, int selfDestructIdx, out UInt256? value)
    {
        HashedKey<(Address, UInt256)> key = new((address, slot));
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(key, out value)) return true;
            if (_persistedSnapshotCount + i <= selfDestructIdx)
            {
                value = null;
                return true;
            }
        }
        if (_persistedSnapshotCount > 0 && persistedSnapshots.TryGetSlot(address, in slot, selfDestructIdx, 0, out value)) return true;
        value = null;
        return false;
    }
}
