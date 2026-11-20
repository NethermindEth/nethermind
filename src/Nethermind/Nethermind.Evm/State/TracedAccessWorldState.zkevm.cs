// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <remarks>
/// SSTORE reads and writes a slot through the decorated state's slot store, recording what <see cref="Get"/> and
/// <see cref="Set(in StorageCell, in UInt256, in UInt256)"/> record. Without a slot store underneath, or in a parallel
/// slice, whose reads resolve against the slice's own changes, it reads and writes through those members instead.
/// </remarks>
public partial class TracedAccessWorldState : IGuestSlotStore
{
    private IGuestSlotStore? _slots;
    private bool _slotsResolved;

    int IGuestSlotStore.ReadForStore(in StorageCell storageCell, out UInt256 current, out UInt256 original)
    {
        if (!_slotsResolved)
        {
            // Resolved once: a type test against an interface walks the type's interfaces.
            _slots = parallel ? null : State as IGuestSlotStore;
            _slotsResolved = true;
        }

        IGuestSlotStore? slots = _slots;
        if (slots is null)
        {
            Get(in storageCell, out current);
            GetOriginal(in storageCell, out original);
            return -1;
        }

        if (_generatingBlockAccessList is not null &&
            (_lastReadStorageChanges is null || !_lastReadStorageCell.Equals(storageCell)))
        {
            RecordStoreRead(in storageCell);
        }

        return slots.ReadForStore(in storageCell, out current, out original);
    }

    /// <summary>Records the read of a cell other than the last one read, as <see cref="Get"/> does outside a parallel slice.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordStoreRead(in StorageCell storageCell)
    {
        bool covered = ReadCoverage?.TryMark(storageCell) == true;
        AccountChangesAtIndex accountChanges = _generatingBlockAccessList!.RecordReadAndGet(storageCell.Address);
        _lastReadStorageCell = storageCell;
        _lastReadStorageChanges = accountChanges;
        if (!covered) accountChanges.AddStorageReadUnchecked(in storageCell.Index);
    }

    void IGuestSlotStore.WriteAt(int slot, in StorageCell storageCell, in UInt256 current, in UInt256 newValue)
    {
        if (slot < 0)
        {
            Set(in storageCell, in newValue, in current);
            return;
        }

        if (_generatingBlockAccessList is not null)
        {
            AssertCurrentStorageValue(in storageCell, in current);
            if (State.TryGetStorageBeforeClear(in storageCell, out UInt256 beforeClear))
                _generatingBlockAccessList.RecordReadAndGet(storageCell.Address).SeedPreTxStorage(in storageCell.Index, in beforeClear);
            _generatingBlockAccessList.AddStorageChange(in storageCell, in current, in newValue);
        }

        _slots!.WriteAt(slot, in storageCell, in current, in newValue);
    }
}
