// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public sealed partial class SnapshotBundle
{
    internal void GetAccounts(ReadOnlySpan<Address> addresses, Span<Account?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, addresses.Length, nameof(values));
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < addresses.Length; i++)
        {
            HashedKey<Address> key = new(addresses[i]);
            if (_changedAccounts.TryGetValue(key, out Account? account) || _readAccounts.TryGetValue(key, out account)
                || TryGetAccountFromSnapshots(key, out account))
                values[i] = account;
            else
                misses.Add(i);
        }
        if (misses.Count < BatchPersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++) values[misses[i]] = _readOnlySnapshotBundle.GetAccount(addresses[misses[i]]);
            return;
        }

        using ArrayPoolListRef<Address> inputs = new(misses.Count, misses.Count);
        using ArrayPoolListRef<Account?> results = new(misses.Count, misses.Count);
        for (int i = 0; i < misses.Count; i++) inputs[i] = addresses[misses[i]];
        _readOnlySnapshotBundle.GetAccounts(inputs.AsSpan(), results.AsSpan());
        for (int i = 0; i < misses.Count; i++) values[misses[i]] = results[i];
    }

    private bool TryGetAccountFromSnapshots(HashedKey<Address> key, out Account? account)
    {
        for (int i = _snapshots.Count - 1; i >= 0; i--)
            if (_snapshots[i].TryGetAccount(key, out account)) return true;
        account = null;
        return false;
    }

    internal void GetSlots(ReadOnlySpan<(Address Address, int SelfDestructIdx, UInt256 Slot)> requests, Span<UInt256?> values)
    {
        GuardDispose();
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, requests.Length, nameof(values));
        using ArrayPoolListRef<int> misses = new(0);
        for (int i = 0; i < requests.Length; i++)
        {
            ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[i];
            if (TryGetSlotFromCurrentSnapshots(request.Address, in request.Slot, request.SelfDestructIdx, out UInt256? value))
                values[i] = value;
            else
                misses.Add(i);
        }
        if (misses.Count < BatchPersistenceReaderExtensions.MinBatchSize)
        {
            for (int i = 0; i < misses.Count; i++)
            {
                ref readonly (Address Address, int SelfDestructIdx, UInt256 Slot) request = ref requests[misses[i]];
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

    private bool TryGetSlotFromCurrentSnapshots(Address address, in UInt256 slot, int selfDestructIdx, out UInt256? value)
    {
        HashedKey<(Address, UInt256)> key = new((address, slot));
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
