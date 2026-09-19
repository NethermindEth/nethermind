// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Phase0 <c>process_slots</c>/<c>process_slot</c> over the Fulu state.</summary>
/// <remarks>
/// The per-slot state root is computed through <see cref="EpochCache.Hasher"/>, so callers
/// following a state lineage can make it incremental with a <see cref="CachedBeaconStateHasher"/>.
/// </remarks>
public static class SlotProcessing
{
    /// <summary>Advances the state to <paramref name="targetSlot"/>, running epoch processing at epoch boundaries.</summary>
    /// <exception cref="BeaconStateException">The state is already at or past <paramref name="targetSlot"/>.</exception>
    public static void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache)
    {
        if (state.Slot >= targetSlot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {targetSlot}");

        while (state.Slot < targetSlot)
        {
            ProcessSlot(state, cache.Hasher);
            // Process the epoch on the start slot of the next epoch.
            if ((state.Slot + 1) % Presets.SlotsPerEpoch == 0)
                EpochProcessing.ProcessEpoch(state, cache);
            state.Slot++;
        }
    }

    /// <summary>Caches the state root, completes the latest block header, and caches the block root for the current slot.</summary>
    public static void ProcessSlot(BeaconStateFulu state, IBeaconStateHasher hasher)
    {
        // Cache the state root.
        Hash256 previousStateRoot = hasher.HashTreeRoot(state);
        state.StateRoots![(int)(state.Slot % Presets.SlotsPerHistoricalRoot)] = previousStateRoot;

        // Cache the latest block header state root.
        if (state.LatestBlockHeader!.StateRoot == Hash256.Zero)
            state.LatestBlockHeader.StateRoot = previousStateRoot;

        // Cache the block root.
        BeaconBlockHeader.Merkleize(state.LatestBlockHeader, out UInt256 blockRoot);
        state.BlockRoots![(int)(state.Slot % Presets.SlotsPerHistoricalRoot)] = new Hash256(blockRoot.ToLittleEndian());
    }
}
