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

    // The deliberately wrong parent root identifies the real Fulu pipeline by its specific refusal.
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

    // The deliberately wrong parent root identifies the real Gloas pipeline by its specific refusal.
    [Test]
    public void Apply_crosses_the_gloas_boundary_then_delegates_into_the_real_gloas_pipeline()
    {

        BeaconStateFulu fuluState = CreateState(validatorCount: 2048);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 1);
        ulong boundarySlot = Presets.SlotsPerEpoch;
        // Zero bid parent hash chooses the empty-payload path, reaching the deliberately wrong parent root.
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

    // process_slots requires state.slot < slot; only this dispatcher's fork crossing may reach the block slot first.
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

    // With GloasForkEpoch = 0 every epoch targets Gloas, so slot 150 still resolves to Gloas; a
    // Fulu-shaped SignedBeaconBlock can never be the right container for that fork.
    [TestCase(0ul, 150ul, "targets the Gloas fork but was constructed as", TestName = "Apply_throws_when_the_block_was_constructed_with_the_wrong_ssz_shape_for_the_fork_it_targets")]
    // GloasForkEpoch far in the future: the block's own slot (101) targets Fulu, but the state has
    // already (by construction, not by this dispatcher) moved to Gloas.
    [TestCase(1_000_000ul, 101ul, "already crossed into Gloas", TestName = "Apply_throws_for_a_fulu_targeted_block_against_a_state_already_carried_past_the_boundary")]
    public void Apply_refuses_a_fulu_block_when_the_state_and_target_fork_are_inconsistent(ulong gloasForkEpoch, ulong blockSlot, string error)
    {
        BeaconStateGloas gloasState = new() { Slot = 100 };
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(gloasState);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch);
        SignedBeaconBlock block = new() { Message = new BeaconBlock { Slot = blockSlot }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new TestEngineDriver.BodyOnlyNotifier(), spec))!;
        Assert.That(ex.Message, Does.Contain(error));
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
