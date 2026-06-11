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
/// justification bits are cloned. Call <see cref="ApplyTo"/> to commit the result, as the realized
/// epoch processing does.
/// </remarks>
public sealed class JustificationAndFinalizationState(BeaconStateFulu state)
{
    public Checkpoint PreviousJustifiedCheckpoint { get; set; } = state.PreviousJustifiedCheckpoint!;

    public Checkpoint CurrentJustifiedCheckpoint { get; set; } = state.CurrentJustifiedCheckpoint!;

    public Checkpoint FinalizedCheckpoint { get; set; } = state.FinalizedCheckpoint!;

    public BitArray JustificationBits { get; } = new(state.JustificationBits!);

    public void ApplyTo(BeaconStateFulu state)
    {
        state.PreviousJustifiedCheckpoint = PreviousJustifiedCheckpoint;
        state.CurrentJustifiedCheckpoint = CurrentJustifiedCheckpoint;
        state.FinalizedCheckpoint = FinalizedCheckpoint;
        state.JustificationBits = JustificationBits;
    }
}
