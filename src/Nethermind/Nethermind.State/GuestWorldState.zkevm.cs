// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

public sealed partial class GuestWorldState : IGuestSlotStore
{
    int IGuestSlotStore.ReadForStore(in StorageCell storageCell, out UInt256 current, out UInt256 original)
    {
        int index = SlotOf(in storageCell);
        ref SlotEntry slot = ref _slots[index];
        slot.RoundTick = ++_accessTick;
        current = ReadSlot(index);
        original = slot.TxStamp == _txId ? slot.TxOriginal : current;
        return index;
    }

    void IGuestSlotStore.WriteAt(int slot, in UInt256 newValue) => SetSlot(slot, in newValue);
}
