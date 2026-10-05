// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Carries a concrete Fulu or Gloas state through fork-dispatched transitions, including the Gloas fork boundary.
/// </summary>
/// <remarks>
/// The privately constructed hierarchy represents Fulu and Gloas states. Unhandled (state, fork)
/// combinations throw rather than falling back to Fulu. This is a runtime guarantee, not a compile-time
/// one: the tuple match has a discard arm, so adding a fork does not break the build. Add its arms here
/// and in <see cref="GloasForkTransition"/> explicitly.
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
    public abstract Hash256 ParentRoot { get; }
    public abstract ulong ProposerIndex { get; }
    /// <summary>Computes the block root: <c>hash_tree_root</c> of the unsigned <c>message</c>, never of the signed container.</summary>
    public abstract Hash256 ComputeMessageRoot();

    public sealed class OfFulu(SignedBeaconBlock block) : ForkedSignedBeaconBlock
    {
        public SignedBeaconBlock Block { get; } = block;
        public override ulong Slot => Block.Message!.Slot;
        public override Hash256 ParentRoot => Block.Message!.ParentRoot!;
        public override ulong ProposerIndex => Block.Message!.ProposerIndex;
        public override Hash256 ComputeMessageRoot() => SszRoots.HashTreeRoot(Block.Message!);
    }

    public sealed class OfGloas(SignedBeaconBlockGloas block) : ForkedSignedBeaconBlock
    {
        public SignedBeaconBlockGloas Block { get; } = block;
        public override ulong Slot => Block.Message!.Slot;
        public override Hash256 ParentRoot => Block.Message!.ParentRoot!;
        public override ulong ProposerIndex => Block.Message!.ProposerIndex;
        public override Hash256 ComputeMessageRoot() => SszRoots.HashTreeRoot(Block.Message!);
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
    /// The Gloas analogue of <see cref="StateTransition.Apply"/>: advances slots through
    /// <see cref="GloasSlotProcessing.ProcessSlots(BeaconStateGloas, ulong, EpochCache)"/> with the caller's
    /// <paramref name="cache"/>, running epoch processing at every boundary crossed, verifies the proposer
    /// signature, runs <see cref="GloasBlockProcessing.ProcessBlock"/>, and validates the claimed post-state root.
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
            GloasSlotProcessing.ProcessSlots(state, block.Slot, cache);

        if (verifySignatures && !GloasBlockProcessing.VerifyProposerSignature(state, signedBlock, pubkeys))
            throw new ProposerSignatureException($"Invalid proposer signature for the block at slot {block.Slot}");

        GloasBlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, spec, verifySignatures);

        if (validateResult && block.StateRoot != cache.Hasher.HashTreeRoot(state))
            throw new BeaconStateException($"Block state root {block.StateRoot} does not match the post-state root");

        return gloas;
    }
}
