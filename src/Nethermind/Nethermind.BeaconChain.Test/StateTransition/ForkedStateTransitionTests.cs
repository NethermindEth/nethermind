// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class ForkedStateTransitionTests
{
    private const ulong Gwei = 1_000_000_000;
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
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), spec, validateResult: false, verifySignatures: false))!;

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
        // block hash (Hash(0x71) below), so process_parent_execution_payload takes its "parent was
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
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfGloas(block), new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), spec, validateResult: false, verifySignatures: false))!;

        Assert.That(ex.Message, Does.Contain("does not match latest header root"));
    }

    [Test]
    public void Apply_throws_when_the_block_was_constructed_with_the_wrong_ssz_shape_for_the_fork_it_targets()
    {
        BeaconStateGloas gloasState = new() { Slot = 100 };
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(gloasState);
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 0);
        // With GloasForkEpoch = 0 every epoch targets Gloas, so slot 50 still resolves to Gloas; a
        // Fulu-shaped SignedBeaconBlock can never be the right container for that fork.
        SignedBeaconBlock block = new() { Message = new BeaconBlock { Slot = 50 }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), spec))!;
        Assert.That(ex.Message, Does.Contain("targets the Gloas fork but was constructed as"));
    }

    [Test]
    public void Apply_throws_for_a_fulu_targeted_block_against_a_state_already_carried_past_the_boundary()
    {
        BeaconStateGloas gloasState = new() { Slot = 100 };
        ForkedBeaconState state = new ForkedBeaconState.OfGloas(gloasState);
        // GloasForkEpoch far in the future: the block's own slot (0) targets Fulu, but the state has
        // already (by construction, not by this dispatcher) moved to Gloas.
        BeaconChainSpec spec = SyntheticSpec(gloasForkEpoch: 1_000_000);
        SignedBeaconBlock block = new() { Message = new BeaconBlock { Slot = 0 }, Signature = default };

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            ForkedStateTransition.Apply(state, new ForkedSignedBeaconBlock.OfFulu(block), new EpochCache(), new PubkeyCache(), new AcceptingNotifier(), spec))!;
        Assert.That(ex.Message, Does.Contain("already crossed into Gloas"));
    }

    private static BeaconChainSpec SyntheticSpec(ulong gloasForkEpoch) => new()
    {
        SecondsPerSlot = 12,
        SlotsPerEpoch = Presets.SlotsPerEpoch,
        GenesisTime = 1_606_824_023,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(Bytes.FromHexString("0x06000000"), 0)],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = gloasForkEpoch,
        GloasForkVersion = GloasVersion,
        Bootnodes = [],
    };

    private static BeaconStateFulu CreateState(int validatorCount)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        Array.Fill(randaoMixes, Hash(0x42));
        Hash256[] blockRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(blockRoots, Hash256.Zero);
        Hash256[] stateRoots = new Hash256[(int)Presets.SlotsPerHistoricalRoot];
        Array.Fill(stateRoots, Hash256.Zero);

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = new Validator
            {
                Pubkey = Pubkey((byte)(0x50 + i)),
                WithdrawalCredentials = Hash256.Zero,
                EffectiveBalance = 32 * Gwei,
                ActivationEpoch = 0,
                ExitEpoch = Presets.FarFutureEpoch,
                WithdrawableEpoch = Presets.FarFutureEpoch,
                ActivationEligibilityEpoch = 0,
            };
            balances[i] = 32 * Gwei;
        }

        return new BeaconStateFulu
        {
            Slot = 0,
            Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = 0, ProposerIndex = 0, ParentRoot = Hash(0x02), StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[validatorCount],
            CurrentEpochParticipation = new byte[validatorCount],
            InactivityScores = new ulong[validatorCount],
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            JustificationBits = new BitArray(4),
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentSyncCommittee = new SyncCommittee(),
            NextSyncCommittee = new SyncCommittee(),
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ParentHash = Hash(0x70), BlockHash = Hash(0x71), PrevRandao = Hash(0x72), GasLimit = 30_000_000 },
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
        };
    }

    /// <summary>A block one slot ahead of <paramref name="state"/>, with the real (state-computed) proposer index.</summary>
    private static SignedBeaconBlock MinimalBlock(BeaconStateFulu state, ulong proposerIndex, Hash256 parentRoot) => new()
    {
        Message = new BeaconBlock
        {
            Slot = state.Slot + 1,
            ProposerIndex = proposerIndex,
            ParentRoot = parentRoot,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBody
            {
                Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512) },
                ExecutionPayload = new ExecutionPayload
                {
                    ParentHash = Hash256.Zero,
                    FeeRecipient = Address.Zero,
                    StateRoot = Hash256.Zero,
                    ReceiptsRoot = Hash256.Zero,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = Hash256.Zero,
                    BlockNumber = state.Slot,
                    GasLimit = 30_000_000,
                    GasUsed = 21_000,
                    Timestamp = 1_750_000_000,
                    ExtraData = [],
                    BaseFeePerGas = 7,
                    BlockHash = Hash256.Zero,
                    Transactions = [],
                    Withdrawals = [],
                    BlobGasUsed = 0,
                    ExcessBlobGas = 0,
                },
                BlsToExecutionChanges = [],
                BlobKzgCommitments = [],
                ExecutionRequests = new ExecutionRequests { Deposits = [], Withdrawals = [], Consolidations = [] },
            },
        },
        Signature = default,
    };

    private static Hash256 Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return new Hash256(bytes);
    }

    private static BlsPublicKey Pubkey(byte value)
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes.AsSpan().Fill(value);
        return new BlsPublicKey(bytes);
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public bool NotifyNewPayload(BeaconBlockBody body) => true;
    }
}
