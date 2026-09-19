// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Wraps whichever concrete beacon state is currently live, so a caller can carry a state across the
/// Gloas fork boundary without the ~160 call sites in <see cref="BlockProcessing"/>,
/// <see cref="EpochProcessing"/>, <see cref="BeaconStateAccessors"/> etc. that take a concrete
/// <see cref="BeaconStateFulu"/> having to become generic or move to an interface.
/// </summary>
/// <remarks>
/// This is the "second fork without duplicating the whole state transition" seam: everything below
/// this type still only knows about <see cref="BeaconStateFulu"/> (the 154-passing-test pipeline is
/// untouched), and everything Gloas-specific — the containers, the upgrade, the fork check — lives
/// beside it rather than threaded through it. The trade-off, made deliberately: this driver cannot yet
/// process a Gloas block (see <see cref="ForkedStateTransition.Apply"/>), only detect and cross the
/// boundary. A generic-parameter or interface-over-state redesign would let Gloas block processing slot
/// in more uniformly later, but would mean rewriting all ~160 sites now for a fork whose own block
/// pipeline (the ePBS split, two-dimensional fork choice) is explicitly out of this task's scope — that
/// is the cost the recon in this task weighed against doing the narrower thing here.
/// <para/>
/// A sealed, privately-constructed hierarchy of exactly the forks this driver can represent
/// (<see cref="BeaconFork"/> lists the same three). <see cref="ForkedStateTransition"/> and
/// <see cref="GloasForkTransition"/> switch over it (and over <see cref="BeaconFork"/> directly)
/// without a discard arm, so adding a fourth fork here without updating every such switch is a
/// compile error under this repo's <c>TreatWarningsAsErrors</c> (CS8509, non-exhaustive switch) —
/// the goal being that an unhandled fork fails the build, not silently falls back to Fulu.
/// </remarks>
public abstract class ForkedBeaconState
{
    private ForkedBeaconState() { }

    public abstract ulong Slot { get; }
    public abstract BeaconFork Fork { get; }

    public sealed class OfFulu(BeaconStateFulu state) : ForkedBeaconState
    {
        public BeaconStateFulu State { get; } = state;
        public override ulong Slot => State.Slot;
        public override BeaconFork Fork => BeaconFork.Fulu;
    }

    public sealed class OfGloas(BeaconStateGloas state) : ForkedBeaconState
    {
        public BeaconStateGloas State { get; } = state;
        public override ulong Slot => State.Slot;
        public override BeaconFork Fork => BeaconFork.Gloas;
    }
}

/// <summary>
/// Fork-dispatching entry point over <see cref="ForkedBeaconState"/>. See the type's own remarks for
/// why this exists instead of a generic or interface-typed state transition.
/// </summary>
public static class ForkedStateTransition
{
    /// <summary>
    /// Advances <paramref name="state"/> to <paramref name="signedBlock"/>'s slot — upgrading it across
    /// the Gloas boundary along the way if that slot range crosses <c>spec.GloasForkEpoch</c> — and then
    /// applies the block.
    /// </summary>
    /// <remarks>
    /// Crossing the boundary itself is fully implemented: slots are advanced under the existing,
    /// unmodified Fulu pipeline right up to the boundary slot, then <see cref="GloasForkTransition.UpgradeToGloas"/>
    /// runs. Applying a block whose target fork is Gloas is not: this driver has no Gloas
    /// <c>ProcessBlock</c> (the ePBS bid/envelope split and its own epoch/slot processing are a
    /// separate, larger piece of work — see the two-dimensional fork choice note in this task's scope).
    /// That gap fails loudly here rather than silently running the Fulu pipeline against a Gloas state
    /// or block.
    /// </remarks>
    /// <exception cref="BeaconStateException">
    /// The block's fork does not match the state actually reached (e.g. a pre-Gloas block against a
    /// state already carried past the boundary), or the state carries a fork this dispatcher does not
    /// (yet) know how to advance further.
    /// </exception>
    public static ForkedBeaconState Apply(
        ForkedBeaconState state,
        SignedBeaconBlock signedBlock,
        EpochCache cache,
        PubkeyCache pubkeys,
        INewPayloadNotifier notifier,
        BeaconChainSpec spec,
        bool validateResult = true,
        bool verifySignatures = true)
    {
        ulong blockEpoch = spec.GetEpoch(signedBlock.Message!.Slot);
        BeaconFork targetFork = spec.ForkAtEpoch(blockEpoch);

        state = CrossBoundaryIfNeeded(state, targetFork, spec, cache);

        return (state, targetFork) switch
        {
            (ForkedBeaconState.OfFulu fulu, BeaconFork.Fulu) => ApplyFulu(fulu, signedBlock, cache, pubkeys, notifier, spec, validateResult, verifySignatures),
            (ForkedBeaconState.OfGloas, BeaconFork.Gloas) => throw new NotSupportedException(
                "Gloas block processing is not implemented: the ePBS split (bid/envelope processing) " +
                "and the two-dimensional fork-choice integration it depends on are separate work. This " +
                "dispatcher only carries a state across the fork boundary (see GloasForkTransition)."),
            (ForkedBeaconState.OfFulu, BeaconFork.Gloas) => throw new BeaconStateException(
                "State is still Fulu after attempting the boundary crossing; the block's slot is not yet at GloasForkEpoch's boundary but claims fork Gloas"),
            (ForkedBeaconState.OfGloas, BeaconFork.Fulu) => throw new BeaconStateException(
                "State has already crossed into Gloas; cannot apply an earlier-fork (Fulu) block against it"),
            (ForkedBeaconState.OfFulu, BeaconFork.Electra) => throw new BeaconStateException(
                "This driver's live pipeline starts at Fulu; an Electra-targeted block cannot be applied to a Fulu state"),
            _ => throw new NotSupportedException($"Unhandled (state, fork) combination: ({state.GetType().Name}, {targetFork})"),
        };
    }

    /// <summary>
    /// If <paramref name="state"/> is still Fulu but <paramref name="targetFork"/> is Gloas, advances it
    /// (via the existing, unmodified <see cref="SlotProcessing.ProcessSlots"/>) to exactly the fork
    /// boundary slot and upgrades it. A no-op otherwise.
    /// </summary>
    private static ForkedBeaconState CrossBoundaryIfNeeded(ForkedBeaconState state, BeaconFork targetFork, BeaconChainSpec spec, EpochCache cache)
    {
        if (state is not ForkedBeaconState.OfFulu { State: var fulu } || targetFork != BeaconFork.Gloas)
            return state;

        ulong boundarySlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(spec.GloasForkEpoch);
        if (fulu.Slot < boundarySlot)
            SlotProcessing.ProcessSlots(fulu, boundarySlot, cache);

        return new ForkedBeaconState.OfGloas(GloasForkTransition.UpgradeToGloas(fulu, spec));
    }

    private static ForkedBeaconState ApplyFulu(
        ForkedBeaconState.OfFulu fulu,
        SignedBeaconBlock signedBlock,
        EpochCache cache,
        PubkeyCache pubkeys,
        INewPayloadNotifier notifier,
        BeaconChainSpec spec,
        bool validateResult,
        bool verifySignatures)
    {
        StateTransition.Apply(fulu.State, signedBlock, cache, pubkeys, notifier, spec, validateResult, verifySignatures);
        return fulu;
    }
}
