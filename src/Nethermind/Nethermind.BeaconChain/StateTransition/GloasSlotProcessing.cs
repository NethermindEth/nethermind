// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Gloas <c>process_slots</c>/<c>process_slot</c> over <see cref="BeaconStateGloas"/>: the per-slot
/// state/block-root bookkeeping, the EIP-7732 payload-availability reset, and
/// <see cref="GloasEpochProcessing.ProcessEpoch"/> at every epoch boundary.
/// </summary>
/// <remarks>
/// The per-slot state root is computed through <see cref="EpochCache.Hasher"/>, so callers
/// following a state lineage can make it incremental with a <see cref="CachedBeaconStateHasher"/>.
/// </remarks>
public static class GloasSlotProcessing
{
    /// <summary>Advances the state to <paramref name="targetSlot"/>, one slot at a time, running epoch processing at boundaries.</summary>
    /// <exception cref="BeaconStateException">The state is already at or past <paramref name="targetSlot"/>.</exception>
    public static void ProcessSlots(BeaconStateGloas state, ulong targetSlot, EpochCache cache)
    {
        if (state.Slot >= targetSlot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

        while (state.Slot < targetSlot)
        {
            ProcessSlot(state, cache.Hasher);
            if ((state.Slot + 1) % Presets.SlotsPerEpoch == 0)
                GloasEpochProcessing.ProcessEpoch(state, cache);
            state.Slot++;
        }
    }

    /// <summary>
    /// <see cref="ProcessSlots(BeaconStateGloas, ulong, EpochCache)"/> with a throwaway
    /// <see cref="EpochCache"/>. Correct but uncached: a caller that owns a per-lineage cache should
    /// pass it instead, so the balance memo and committee shufflings carry across slots.
    /// </summary>
    public static void ProcessSlots(BeaconStateGloas state, ulong targetSlot) => ProcessSlots(state, targetSlot, new EpochCache());

    /// <summary>
    /// Caches the state root, completes the latest block header, caches the block root for the
    /// current slot and, new in Gloas, marks the next slot's payload as not (yet) available.
    /// </summary>
    public static void ProcessSlot(BeaconStateGloas state, IBeaconStateHasher hasher)
    {
        int slotIndex = (int)(state.Slot % Presets.SlotsPerHistoricalRoot);
        Hash256 previousStateRoot = hasher.HashTreeRoot(state);
        state.StateRoots![slotIndex] = previousStateRoot;

        if (state.LatestBlockHeader!.StateRoot == Hash256.Zero)
            state.LatestBlockHeader.StateRoot = previousStateRoot;

        BeaconBlockHeader.Merkleize(state.LatestBlockHeader, out UInt256 blockRoot);
        state.BlockRoots![slotIndex] = new Hash256(blockRoot.ToLittleEndian());

        // The bit is set again only by the child block that declares this slot's payload delivered.
        state.ExecutionPayloadAvailability![(int)((state.Slot + 1) % Presets.SlotsPerHistoricalRoot)] = false;
    }
}
