// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// The fork-choice <c>Store</c> of the consensus spec: tracks the current slot, the
/// justified/finalized checkpoints (realized and unrealized) and the proposer boost root.
/// </summary>
/// <remarks>
/// Only the time-keeping side of the store lives here. The state-transition-driven updates — the
/// unrealized checkpoints computed from post-states in <c>on_block</c> — are supplied by the caller
/// via <see cref="UpdateUnrealizedCheckpoints"/>; that integration arrives with the state-transition
/// milestones.
/// </remarks>
/// <param name="slotsPerEpoch">Number of slots per epoch.</param>
/// <param name="currentSlot">The slot at creation time.</param>
/// <param name="justifiedCheckpoint">The justified checkpoint of the anchor state.</param>
/// <param name="finalizedCheckpoint">The finalized checkpoint of the anchor state.</param>
public sealed class ForkChoiceStore(ulong slotsPerEpoch, ulong currentSlot, CheckpointRef justifiedCheckpoint, CheckpointRef finalizedCheckpoint)
{
    public ulong CurrentSlot { get; private set; } = currentSlot;

    public ulong CurrentEpoch => CurrentSlot / slotsPerEpoch;

    public CheckpointRef JustifiedCheckpoint { get; private set; } = justifiedCheckpoint;

    public CheckpointRef FinalizedCheckpoint { get; private set; } = finalizedCheckpoint;

    public CheckpointRef UnrealizedJustifiedCheckpoint { get; private set; } = justifiedCheckpoint;

    public CheckpointRef UnrealizedFinalizedCheckpoint { get; private set; } = finalizedCheckpoint;

    /// <summary>The root receiving the proposer score boost; <see cref="Hash256.Zero"/> when no boost applies.</summary>
    public Hash256 ProposerBoostRoot { get; set; } = Hash256.Zero;

    /// <summary>
    /// Advances the store to <paramref name="slot"/>, performing the per-slot processing of the
    /// spec's <c>on_tick</c> for every slot crossed: the proposer boost is reset at the start of
    /// each slot, and the unrealized checkpoints are pulled up at each epoch boundary.
    /// </summary>
    /// <remarks>A no-op when <paramref name="slot"/> is not later than <see cref="CurrentSlot"/>.</remarks>
    public void OnTick(ulong slot)
    {
        while (CurrentSlot < slot)
        {
            CurrentSlot++;
            // The proposer boost only lasts for the slot in which the boosted block arrived.
            ProposerBoostRoot = Hash256.Zero;

            if (CurrentSlot % slotsPerEpoch == 0)
            {
                PullUpUnrealizedCheckpoints();
            }
        }
    }

    /// <summary>Adopts the unrealized checkpoints as realized; the epoch-boundary "pull-up" of the spec's <c>on_tick_per_slot</c>.</summary>
    public void PullUpUnrealizedCheckpoints() => UpdateCheckpoints(UnrealizedJustifiedCheckpoint, UnrealizedFinalizedCheckpoint);

    /// <summary>Updates the realized checkpoints if the candidates are from later epochs; the spec's <c>update_checkpoints</c>.</summary>
    public void UpdateCheckpoints(CheckpointRef justified, CheckpointRef finalized)
    {
        if (justified.Epoch > JustifiedCheckpoint.Epoch) JustifiedCheckpoint = justified;
        if (finalized.Epoch > FinalizedCheckpoint.Epoch) FinalizedCheckpoint = finalized;
    }

    /// <summary>Updates the unrealized checkpoints if the candidates are from later epochs; the spec's <c>update_unrealized_checkpoints</c>.</summary>
    /// <remarks>Hook for block processing: called with the unrealized checkpoints computed from each new block's post-state.</remarks>
    public void UpdateUnrealizedCheckpoints(CheckpointRef unrealizedJustified, CheckpointRef unrealizedFinalized)
    {
        if (unrealizedJustified.Epoch > UnrealizedJustifiedCheckpoint.Epoch) UnrealizedJustifiedCheckpoint = unrealizedJustified;
        if (unrealizedFinalized.Epoch > UnrealizedFinalizedCheckpoint.Epoch) UnrealizedFinalizedCheckpoint = unrealizedFinalized;
    }
}
