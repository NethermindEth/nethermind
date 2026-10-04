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
public static partial class SlotProcessing
{
    public static partial void ProcessSlots(BeaconStateFulu state, ulong targetSlot, EpochCache cache);

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
