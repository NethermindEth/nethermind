// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

public static partial class SlotProcessing
{
    /// <summary>Advances the state to <paramref name="targetSlot"/>, running epoch processing at epoch boundaries.</summary>
    /// <exception cref="BeaconStateException">The state is already at or past <paramref name="targetSlot"/>.</exception>
    public static partial void ProcessSlots(ForkState state, ulong targetSlot, EpochCache cache)
    {
        if (state.Slot >= targetSlot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

        while (state.Slot < targetSlot)
        {
            ProcessSlot(state, cache.Hasher);
            // Process the epoch on the start slot of the next epoch.
            if ((state.Slot + 1) % Presets.SlotsPerEpoch == 0)
                ForkEpochProcessing.ProcessEpoch(state, cache);
            state.Slot++;
        }
    }
}
