// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// The fields written by <c>process_justification_and_finalization</c>, detached from the state so
/// the computation can run without mutating (or cloning) it. Ported from Lighthouse's
/// <c>JustificationAndFinalizationState</c>.
/// </summary>
/// <remarks>
/// Fork choice uses this to compute a block's <em>unrealized</em> checkpoints (the spec's
/// <c>compute_pulled_up_tip</c>) from its post-state without the state copy the spec performs.
/// The <see cref="Checkpoint"/> instances are shared with the state and treated as immutable; the
/// justification bits are cloned. Call <see cref="ApplyTo(BeaconStateFulu)"/> to commit the result, as the realized
/// epoch processing does.
/// </remarks>
public sealed class JustificationAndFinalizationState
{
    /// <summary>Detaches the justification and finalization fields of a Fulu <paramref name="state"/>, cloning its justification bits.</summary>
    public JustificationAndFinalizationState(BeaconStateFulu state)
        : this(state.PreviousJustifiedCheckpoint!, state.CurrentJustifiedCheckpoint!, state.FinalizedCheckpoint!, state.JustificationBits!)
    {
    }

    /// <summary>Detaches the justification and finalization fields of a Gloas <paramref name="state"/>, cloning its justification bits.</summary>
    public JustificationAndFinalizationState(BeaconStateGloas state)
        : this(state.PreviousJustifiedCheckpoint!, state.CurrentJustifiedCheckpoint!, state.FinalizedCheckpoint!, state.JustificationBits!)
    {
    }

    private JustificationAndFinalizationState(Checkpoint previousJustified, Checkpoint currentJustified, Checkpoint finalized, BitArray justificationBits)
    {
        PreviousJustifiedCheckpoint = previousJustified;
        CurrentJustifiedCheckpoint = currentJustified;
        FinalizedCheckpoint = finalized;
        JustificationBits = new BitArray(justificationBits);
    }

    public Checkpoint PreviousJustifiedCheckpoint { get; set; }

    public Checkpoint CurrentJustifiedCheckpoint { get; set; }

    public Checkpoint FinalizedCheckpoint { get; set; }

    public BitArray JustificationBits { get; }

    public void ApplyTo(BeaconStateFulu state)
    {
        state.PreviousJustifiedCheckpoint = PreviousJustifiedCheckpoint;
        state.CurrentJustifiedCheckpoint = CurrentJustifiedCheckpoint;
        state.FinalizedCheckpoint = FinalizedCheckpoint;
        state.JustificationBits = JustificationBits;
    }

    /// <summary>Writes these fields back to a Gloas <paramref name="state"/>, as <c>process_justification_and_finalization</c> does.</summary>
    public void ApplyTo(BeaconStateGloas state)
    {
        state.PreviousJustifiedCheckpoint = PreviousJustifiedCheckpoint;
        state.CurrentJustifiedCheckpoint = CurrentJustifiedCheckpoint;
        state.FinalizedCheckpoint = FinalizedCheckpoint;
        state.JustificationBits = JustificationBits;
    }
}
