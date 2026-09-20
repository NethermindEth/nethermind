// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Phase0 <c>process_slots</c>/<c>process_slot</c> over <see cref="BeaconStateGloas"/>: the
/// per-slot state/block-root bookkeeping every block application needs, ported unchanged from
/// <see cref="SlotProcessing"/> (Gloas made no changes to this step).
/// </summary>
/// <remarks>
/// Deliberately narrower than <see cref="SlotProcessing"/>: it advances a single Gloas block's one
/// slot but refuses to cross an epoch boundary, because Gloas epoch processing (justification,
/// rewards, the registry updates <see cref="EpochProcessing"/> runs for Fulu) is not implemented
/// for <see cref="BeaconStateGloas"/> and is out of this task's scope. Running only the per-slot
/// half at an epoch boundary would silently produce a state consensus-specs never reaches - fail
/// loudly by name instead. It also hashes with the plain <see cref="SszRoots.HashTreeRoot{T}"/>
/// rather than <see cref="EpochCache.Hasher"/>'s incremental cache: that cache is built around
/// Fulu's field layout, and Gloas's progressive-container shape is a different, larger piece of
/// work this task does not need (state hashing correctness, not speed, is what is in scope here).
/// </remarks>
public static class GloasSlotProcessing
{
    /// <summary>Advances the state to <paramref name="targetSlot"/>, one slot at a time.</summary>
    /// <exception cref="BeaconStateException">The state is already at or past <paramref name="targetSlot"/>.</exception>
    /// <exception cref="NotSupportedException">Advancing would cross an epoch boundary.</exception>
    public static void ProcessSlots(BeaconStateGloas state, ulong targetSlot)
    {
        if (state.Slot >= targetSlot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

        while (state.Slot < targetSlot)
        {
            if ((state.Slot + 1) % Presets.SlotsPerEpoch == 0)
                throw new NotSupportedException(
                    $"Advancing past slot {state.Slot} would cross an epoch boundary; Gloas epoch processing " +
                    "(justification, rewards, registry updates, builder-pending-payment rotation) is not implemented");

            ProcessSlot(state);
            state.Slot++;
        }
    }

    /// <summary>Caches the state root, completes the latest block header, and caches the block root for the current slot.</summary>
    public static void ProcessSlot(BeaconStateGloas state)
    {
        Hash256 previousStateRoot = SszRoots.HashTreeRoot(state);
        state.StateRoots![(int)(state.Slot % Presets.SlotsPerHistoricalRoot)] = previousStateRoot;

        if (state.LatestBlockHeader!.StateRoot == Hash256.Zero)
            state.LatestBlockHeader.StateRoot = previousStateRoot;

        BeaconBlockHeader.Merkleize(state.LatestBlockHeader, out UInt256 blockRoot);
        state.BlockRoots![(int)(state.Slot % Presets.SlotsPerHistoricalRoot)] = new Hash256(blockRoot.ToLittleEndian());
    }
}
