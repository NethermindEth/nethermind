// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>A count of work items allowed per wall-clock slot, renewed when the slot changes; safe to call from several network threads.</summary>
internal sealed class PerSlotBudget(int perSlot)
{
    private readonly Lock _lock = new();
    private ulong _slot;
    private int _taken;

    public bool TryTake(ulong currentSlot)
    {
        lock (_lock)
        {
            if (currentSlot != _slot)
            {
                _slot = currentSlot;
                _taken = 0;
            }

            if (_taken >= perSlot)
            {
                return false;
            }

            _taken++;
            return true;
        }
    }
}
