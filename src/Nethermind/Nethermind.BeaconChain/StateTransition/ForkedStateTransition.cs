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
/// untouched), and everything Gloas-specific - the containers, the upgrade, the fork check - lives
/// beside it rather than threaded through it. The trade-off, made deliberately: this driver cannot yet
/// process a Gloas block (see <see cref="ForkedStateTransition.Apply"/>), only detect and cross the
/// boundary. A generic-parameter or interface-over-state redesign would let Gloas block processing slot
/// in more uniformly later, but would mean rewriting all ~160 sites now for a fork whose own block
/// pipeline (the ePBS split, two-dimensional fork choice) is explicitly out of this task's scope - that
/// is the cost the recon in this task weighed against doing the narrower thing here.
/// <para/>
/// A sealed, privately-constructed hierarchy of exactly the forks this driver can represent
/// (<see cref="BeaconFork"/> lists the same three). Every (state, fork) pairing is matched
/// explicitly and the final arm throws, so an unhandled fork fails loudly at runtime rather than
/// silently falling back to Fulu. This is a runtime guarantee, not a compile-time one: the tuple
/// match carries a discard arm for the combinations that cannot occur, so adding a fork will not
/// break the build. Add the new arms here and in <see cref="GloasForkTransition"/> by hand.
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
/// Wraps whichever concrete signed block shape a caller has decoded, mirroring
/// <see cref="ForkedBeaconState"/> on the block side: <see cref="ForkedStateTransition.Apply"/> needs
/// to know which SSZ shape (Fulu's <c>SignedBeaconBlock</c> or Gloas's
/// <see cref="SignedBeaconBlockGloas"/>, an entirely different body - the bid/envelope split, not an
/// additive change) it was actually given, since the two are unrelated types, not a fork-versioned
/// view of the same one.
/// </summary>
public abstract class ForkedSignedBeaconBlock
{
    private ForkedSignedBeaconBlock() { }

    public abstract ulong Slot { get; }

    public sealed class OfFulu(SignedBeaconBlock block) : ForkedSignedBeaconBlock
    {
        public SignedBeaconBlock Block { get; } = block;
        public override ulong Slot => Block.Message!.Slot;
    }

    public sealed class OfGloas(SignedBeaconBlockGloas block) : ForkedSignedBeaconBlock
    {
        public SignedBeaconBlockGloas Block { get; } = block;
        public override ulong Slot => Block.Message!.Slot;
    }
}

/// <summary>
/// Fork-dispatching entry point over <see cref="ForkedBeaconState"/>. See the type's own remarks for
/// why this exists instead of a generic or interface-typed state transition.
/// </summary>
public static class ForkedStateTransition
{
    /// <summary>
    /// Advances <paramref name="state"/> to <paramref name="signedBlock"/>'s slot - upgrading it across
    /// the Gloas boundary along the way if that slot range crosses <c>spec.GloasForkEpoch</c> - and then
    /// applies the block.
    /// </summary>
    /// <remarks>
    /// Crossing the boundary itself is fully implemented: slots are advanced under the existing,
    /// unmodified Fulu pipeline right up to the boundary slot, then <see cref="GloasForkTransition.UpgradeToGloas"/>
    /// runs. Applying a Gloas-targeted block now runs the real ePBS pipeline (see
    /// <see cref="GloasBlockProcessing"/>); see that type's remarks for exactly which parts of it are
    /// implemented versus a declared, by-name gap.
    /// </remarks>
    /// <exception cref="BeaconStateException">
    /// The state is not behind the block's slot, the block's fork does not match the state actually reached (e.g. a pre-Gloas block against a
    /// state already carried past the boundary), the block was constructed with the wrong SSZ shape
    /// for the fork its slot targets, or the state carries a fork this dispatcher does not know how
    /// to advance further.
    /// </exception>
    public static ForkedBeaconState Apply(
        ForkedBeaconState state,
        ForkedSignedBeaconBlock signedBlock,
        EpochCache cache,
        PubkeyCache pubkeys,
        INewPayloadNotifier notifier,
        BeaconChainSpec spec,
        bool validateResult = true,
        bool verifySignatures = true)
    {
        // process_slots asserts state.slot < block.slot before any slot is processed, whichever fork the block targets.
        if (state.Slot >= signedBlock.Slot)
            throw new BeaconStateException($"Cannot advance state at slot {state.Slot} to non-future slot {signedBlock.Slot}");

        ulong blockEpoch = spec.GetEpoch(signedBlock.Slot);
        BeaconFork targetFork = spec.ForkAtEpoch(blockEpoch);

        state = CrossBoundaryIfNeeded(state, targetFork, spec, cache);

        return (state, targetFork) switch
        {
            (ForkedBeaconState.OfFulu fulu, BeaconFork.Fulu) => ApplyFulu(fulu, RequireFulu(signedBlock), cache, pubkeys, notifier, spec, validateResult, verifySignatures),
            (ForkedBeaconState.OfGloas gloas, BeaconFork.Gloas) => ApplyGloas(gloas, RequireGloas(signedBlock), cache, pubkeys, notifier, spec, validateResult, verifySignatures),
            (ForkedBeaconState.OfFulu, BeaconFork.Gloas) => throw new BeaconStateException(
                "State is still Fulu after attempting the boundary crossing; the block's slot is not yet at GloasForkEpoch's boundary but claims fork Gloas"),
            (ForkedBeaconState.OfGloas, BeaconFork.Fulu) => throw new BeaconStateException(
                "State has already crossed into Gloas; cannot apply an earlier-fork (Fulu) block against it"),
            (ForkedBeaconState.OfFulu, BeaconFork.Electra) => throw new BeaconStateException(
                "This driver's live pipeline starts at Fulu; an Electra-targeted block cannot be applied to a Fulu state"),
            _ => throw new NotSupportedException($"Unhandled (state, fork) combination: ({state.GetType().Name}, {targetFork})"),
        };
    }

    private static SignedBeaconBlock RequireFulu(ForkedSignedBeaconBlock block) =>
        block is ForkedSignedBeaconBlock.OfFulu fulu
            ? fulu.Block
            : throw new BeaconStateException($"Block at slot {block.Slot} targets the Fulu fork but was constructed as {block.GetType().Name}");

    private static SignedBeaconBlockGloas RequireGloas(ForkedSignedBeaconBlock block) =>
        block is ForkedSignedBeaconBlock.OfGloas gloas
            ? gloas.Block
            : throw new BeaconStateException($"Block at slot {block.Slot} targets the Gloas fork but was constructed as {block.GetType().Name}");

    /// <summary>
    /// If <paramref name="state"/> is still Fulu but <paramref name="targetFork"/> is Gloas, advances it
    /// (via the existing, unmodified <see cref="SlotProcessing.ProcessSlots"/>) to exactly the fork
    /// boundary slot and upgrades it. A no-op otherwise.
    /// </summary>
    internal static ForkedBeaconState CrossBoundaryIfNeeded(ForkedBeaconState state, BeaconFork targetFork, BeaconChainSpec spec, EpochCache cache)
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

    /// <summary>
    /// The Gloas analogue of <see cref="StateTransition.Apply"/>: advances slots (refusing to cross
    /// an epoch boundary - see <see cref="GloasSlotProcessing"/>), verifies the proposer signature,
    /// runs <see cref="GloasBlockProcessing.ProcessBlock"/>, and validates the claimed post-state root.
    /// </summary>
    /// <remarks>
    /// Skips slot advancement when <c>state.Slot == block.Slot</c>, which <see cref="Apply"/> admits only
    /// for a block at the fork boundary slot that <see cref="CrossBoundaryIfNeeded"/> has just advanced
    /// the state to: <see cref="Apply"/> has already asserted <c>process_slots</c>'s strict
    /// <c>state.slot &lt; block.slot</c> against the state it was given.
    /// </remarks>
    private static ForkedBeaconState ApplyGloas(
        ForkedBeaconState.OfGloas gloas,
        SignedBeaconBlockGloas signedBlock,
        EpochCache cache,
        PubkeyCache pubkeys,
        INewPayloadNotifier notifier,
        BeaconChainSpec spec,
        bool validateResult,
        bool verifySignatures)
    {
        BeaconStateGloas state = gloas.State;
        BeaconBlockGloas block = signedBlock.Message!;

        if (state.Slot < block.Slot)
            GloasSlotProcessing.ProcessSlots(state, block.Slot);

        if (verifySignatures && !GloasBlockProcessing.VerifyProposerSignature(state, signedBlock, pubkeys))
            throw new BeaconStateException($"Invalid proposer signature for the block at slot {block.Slot}");

        GloasBlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, spec, verifySignatures);

        if (validateResult && block.StateRoot != SszRoots.HashTreeRoot(state))
            throw new BeaconStateException($"Block state root {block.StateRoot} does not match the post-state root");

        return gloas;
    }
}
