// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Engine;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class ForkedStateTransitionTests
{
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    /// <summary>
    /// Rather than build a fully-valid block (a real proposer signature, a real post-state root, ...),
    /// this deliberately breaks one check (the parent root) and asserts on that check's own exception
    /// text. Only the real, unmodified <see cref="BlockProcessing.ProcessBlockHeader"/> raises that
    /// exact message, so seeing it is proof this dispatcher actually delegated into the real Fulu
    /// pipeline rather than silently no-op'ing or running some Gloas-shaped stand-in.
    /// </summary>
    [Test]
    public void Apply_delegates_into_the_real_fulu_pipeline_when_the_target_fork_is_still_fulu()
    {
        BeaconStateFulu fuluState = CreateState(validatorCount: 8);
        ulong expectedProposer = fuluState.GetBeaconProposerIndex(1);
        SignedBeaconBlock block = MinimalBlock(fuluState, expectedProposer, parentRoot: Hash256.Zero);
        ForkedBeaconState state = new ForkedBeaconState.OfFulu(fuluState);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 1_000_000);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec, validateResult: false, verifySignatures: false))!;

        Assert.That(ex.Message, Does.Contain("parent root"));
    }

    /// <summary>
    /// Gloas block processing is real now (see <see cref="Nethermind.BeaconChain.Test.StateTransition.GloasBlockProcessingTests"/>
    /// for the full, valid-transition coverage), so this test keeps the same "deliberately break one
    /// check, assert on that check's own exact message" style as the Fulu test above: only the real
    /// <see cref="GloasBlockProcessing.ProcessBlockHeader"/> raises this exact text, which is proof
    /// the dispatcher crossed the boundary and delegated into the real Gloas pipeline rather than
    /// silently no-op'ing.
    /// </summary>
    [Test]
    public void Apply_crosses_the_gloas_boundary_then_delegates_into_the_real_gloas_pipeline()
    {
        // GloasForkEpoch = 1 means the boundary slot is SLOTS_PER_EPOCH (32); a block at that slot
        // targets Gloas, and sits exactly at the boundary slot the crossing already advanced to.
        BeaconStateFulu fuluState = CreateState(validatorCount: 2048);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 1);
        ulong boundarySlot = Presets.SlotsPerEpoch;
        // A default (zero) bid parent block hash cannot match the fork-upgrade placeholder bid's
        // block hash (0x71-filled in the fixture), so process_parent_execution_payload takes its "parent was
        // empty" path - a true no-op given empty parent execution requests - before process_block_header
        // runs and rejects the deliberately wrong (zero) parent root.
        SignedBeaconBlockGloas block = new()
        {
            Message = new BeaconBlockGloas
            {
                Slot = boundarySlot,
                ParentRoot = Hash256.Zero,
                Body = new BeaconBlockBodyGloas { SignedExecutionPayloadBid = new SignedExecutionPayloadBid { Message = new ExecutionPayloadBid() } },
            },
            Signature = default,
        };
        ForkedBeaconState state = new ForkedBeaconState.OfFulu(fuluState);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfGloas(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec, validateResult: false, verifySignatures: false))!;

        Assert.That(ex.Message, Does.Contain("does not match latest header root"));
    }

    /// <summary>
    /// consensus-specs <c>process_slots</c> asserts <c>state.slot &lt; slot</c>, so a Gloas block at the slot
    /// the given state already sits at is invalid (sanity vector <c>invalid_same_slot_block_transition</c>),
    /// even at the fork boundary slot, where only a crossing made by <see cref="ForkedStateTransition.Apply"/>
    /// itself may leave the state at the block's slot.
    /// </summary>
    [Test]
    public void Apply_rejects_a_gloas_block_at_the_slot_the_state_already_sits_at([Values] bool stateIsStillFulu)
    {
        ulong boundarySlot = Presets.SlotsPerEpoch;
        BeaconStateFulu fuluState = CreateState(validatorCount: 8);
        fuluState.Slot = boundarySlot;
        ForkedBeaconState state = stateIsStillFulu
            ? new ForkedBeaconState.OfFulu(fuluState)
            : new ForkedBeaconState.OfGloas(new BeaconStateGloas { Slot = boundarySlot });
        SignedBeaconBlockGloas block = new() { Message = new BeaconBlockGloas { Slot = boundarySlot }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfGloas(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), SyntheticSpec(gloasForkEpoch: 1), validateResult: false, verifySignatures: false))!;

        Assert.That(ex.Message, Does.Contain("non-future slot"));
    }

    [Test]
    public void Apply_throws_when_the_block_was_constructed_with_the_wrong_ssz_shape_for_the_fork_it_targets()
    {
        BeaconStateGloas gloasState = new() { Slot = 100 };
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(gloasState);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 0);
        // With GloasForkEpoch = 0 every epoch targets Gloas, so slot 150 still resolves to Gloas; a
        // Fulu-shaped SignedBeaconBlock can never be the right container for that fork.
        SignedBeaconBlock block = new() { Message = new BeaconBlock { Slot = 150 }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec))!;
        Assert.That(ex.Message, Does.Contain("targets the Gloas fork but was constructed as"));
    }

    [Test]
    public void Apply_throws_for_a_fulu_targeted_block_against_a_state_already_carried_past_the_boundary()
    {
        BeaconStateGloas gloasState = new() { Slot = 100 };
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(gloasState);
        // GloasForkEpoch far in the future: the block's own slot (101) targets Fulu, but the state has
        // already (by construction, not by this dispatcher) moved to Gloas.
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 1_000_000);
        SignedBeaconBlock block = new() { Message = new BeaconBlock { Slot = 101 }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec))!;
        Assert.That(ex.Message, Does.Contain("already crossed into Gloas"));
    }

    private static BeaconChainSpec SyntheticSpec(ulong gloasForkEpoch) => GloasTestFixtures.SyntheticSpec(gloasForkEpoch, GloasVersion);

    private static BeaconStateFulu CreateState(int validatorCount)
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(validatorCount == 0 ? 1 : validatorCount);
        state.GenesisTime = 0;
        state.GenesisValidatorsRoot = null;
        state.HistoricalRoots = null;
        state.Eth1DataVotes = null;
        state.FinalizedCheckpoint!.Epoch = 0;
        state.CurrentSyncCommittee = new SyncCommittee();
        state.NextSyncCommittee = new SyncCommittee();
        state.PendingDeposits = null;
        state.PendingPartialWithdrawals = null;
        state.PendingConsolidations = null;
        if (validatorCount == 0)
        {
            state.Validators = new Validator[validatorCount];
            state.Balances = new ulong[validatorCount];
            state.PreviousEpochParticipation = new byte[validatorCount];
            state.CurrentEpochParticipation = new byte[validatorCount];
            state.InactivityScores = new ulong[validatorCount];
        }
        return state;
    }

    /// <summary>A block one slot ahead of <paramref name="state"/>, with the real (state-computed) proposer index.</summary>
    private static SignedBeaconBlock MinimalBlock(BeaconStateFulu state, ulong proposerIndex, Hash256 parentRoot)
    {
        SignedBeaconBlock block = SignedBeaconBlockBuilders.CreateMinimalBlock(state.Slot + 1);
        block.Message!.ProposerIndex = proposerIndex;
        block.Message.ParentRoot = parentRoot;
        ExecutionPayload payload = block.Message.Body!.ExecutionPayload!;
        payload.BlockNumber = state.Slot;
        payload.ExtraData = [];
        return block;
    }
}
