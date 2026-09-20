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
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// Real state transitions through the Gloas block-processing pipeline (<see cref="GloasBlockProcessing"/>):
/// the bid/envelope split, withdrawals computed from state, and the builder-payment machinery for a
/// payload settled within the epoch it was committed in. Every fixture uses distinctive, non-zero
/// values for whatever field its assertion checks - an assertion that would pass against a dropped
/// or zeroed field is not a real assertion.
/// </summary>
public class GloasBlockProcessingTests
{
    private const ulong Gwei = 1_000_000_000;
    // Large enough that every (slot, committee) slice in a 32-slot epoch gets at least one member
    // during UpgradeToGloas's PTC-window initialization; with too few validators most slices are
    // empty and ComputeBalanceWeightedSelection has nothing to sample from.
    private const int ValidatorCount = 2048;
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    // ---- Bid processing: signature verification actually gates state mutation ----

    [Test]
    public void ProcessExecutionPayloadBid_rejects_a_bid_whose_signature_does_not_match_the_registered_builder()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei);
        // Flip a byte of the otherwise-valid signature: still 96 well-formed bytes, just the wrong one.
        byte[] corrupted = (byte[])bid.Signature.Bytes.ToArray().Clone();
        corrupted[10] ^= 0xFF;
        bid.Signature = new BlsSignature(corrupted);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain("Invalid execution payload bid signature"));
        Assert.That(state.LatestExecutionPayloadBid!.BuilderIndex, Is.EqualTo(Presets.BuilderIndexSelfBuild),
            "a rejected bid must never be committed to state");
    }

    [Test]
    public void ProcessExecutionPayloadBid_accepts_a_correctly_signed_builder_bid_and_records_its_pending_payment()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        const ulong value = 7 * Gwei;
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: value);

        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);

        Assert.Multiple(() =>
        {
            Assert.That(state.LatestExecutionPayloadBid, Is.SameAs(bid.Message));
            BuilderPendingPayment payment = state.BuilderPendingPayments![(int)(Presets.SlotsPerEpoch + bid.Message!.Slot % Presets.SlotsPerEpoch)];
            Assert.That(payment.Withdrawal!.Amount, Is.EqualTo(value));
            Assert.That(payment.Withdrawal.BuilderIndex, Is.EqualTo(0ul));
            Assert.That(payment.Withdrawal.FeeRecipient, Is.EqualTo(bid.Message.FeeRecipient));
        });
    }

    [Test]
    public void ProcessExecutionPayloadBid_rejects_a_builder_who_cannot_cover_the_bid()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        // The builder's whole balance is 40 Gwei (see CreateGloasState); a bid above balance minus
        // MIN_DEPOSIT_AMOUNT must be rejected rather than driving the builder's balance negative.
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 100 * Gwei);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true))!;
        Assert.That(ex.Message, Does.Contain("cannot cover a bid"));
    }

    // ---- Envelope verification: a pure check against the committed bid, no state mutation ----

    [Test]
    public void VerifyExecutionPayloadEnvelope_rejects_an_envelope_whose_payload_does_not_match_the_committed_bid()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);

        // Sign a self-consistent envelope built against a gas limit the actually-committed bid never
        // used - the signature is genuinely valid (it covers exactly this envelope's own content),
        // so this exercises the cross-check against state.latest_execution_payload_bid, not signing.
        ExecutionPayloadBid differentGasLimit = new()
        {
            ParentBlockHash = bid.Message!.ParentBlockHash,
            ParentBlockRoot = bid.Message.ParentBlockRoot,
            BlockHash = bid.Message.BlockHash,
            PrevRandao = bid.Message.PrevRandao,
            FeeRecipient = bid.Message.FeeRecipient,
            GasLimit = bid.Message.GasLimit + 1,
            BuilderIndex = bid.Message.BuilderIndex,
            Slot = bid.Message.Slot,
            Value = bid.Message.Value,
            BlobKzgCommitments = bid.Message.BlobKzgCommitments,
            ExecutionRequestsRoot = bid.Message.ExecutionRequestsRoot,
        };
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, differentGasLimit, builderSk, builderIndex: 0);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.VerifyExecutionPayloadEnvelope(state, envelope, new AcceptingNotifier(), new PubkeyCache()))!;
        Assert.That(ex.Message, Does.Contain("does not match the committed bid"));
    }

    [Test]
    public void VerifyExecutionPayloadEnvelope_accepts_a_matching_envelope_and_mutates_nothing()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        Hash256 latestBlockHashBefore = state.LatestBlockHash!;
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(state, envelope, new AcceptingNotifier(), new PubkeyCache()));

        Assert.That(state.LatestBlockHash, Is.EqualTo(latestBlockHashBefore), "verification must not apply the payload - that happens one block later");
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore));
    }

    // ---- The two-step apply: bid committed in one block, applied and paid out in the next ----

    [Test]
    public void The_two_step_apply_settles_and_pays_the_builder_exactly_one_block_after_the_bid_was_committed()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance);
        const ulong bidValue = 9 * Gwei;
        BeaconChainSpec spec = SyntheticSpec();
        EpochCache cache = new();

        Hash256 rootBeforeBlock1 = SszRoots.HashTreeRoot(state);

        // Block 1: the builder's bid is committed (a pending payment is recorded, nothing paid yet).
        SignedExecutionPayloadBid bid1 = ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue);
        SignedBeaconBlockGloas block1 = MinimalBlock(state, bid1);
        GloasBlockProcessing.ProcessBlock(state, block1.Message!, cache, new PubkeyCache(), new AcceptingNotifier(), spec, verifySignatures: false);

        Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance), "committing a bid must not yet move any balance");
        ulong paymentIndex = Presets.SlotsPerEpoch + block1.Message!.Slot % Presets.SlotsPerEpoch;
        Assert.That(state.BuilderPendingPayments![(int)paymentIndex].Withdrawal!.Amount, Is.EqualTo(bidValue));

        // The hash tree root is not just a stand-in for "some field changed": it must actually be a
        // pure, deterministic function of the mutated state (two independent recomputations agree),
        // and it must differ once real mutations (the committed bid, the new header, RANDAO, the
        // sync aggregate's balance changes) have actually landed.
        Hash256 rootAfterBlock1First = SszRoots.HashTreeRoot(state);
        Hash256 rootAfterBlock1Second = SszRoots.HashTreeRoot(state);
        Assert.That(rootAfterBlock1First, Is.EqualTo(rootAfterBlock1Second), "hash_tree_root must be a pure function of the state, not vary call to call");
        Assert.That(rootAfterBlock1First, Is.Not.EqualTo(rootBeforeBlock1), "processing block 1 must actually have mutated the state");

        // The envelope for block 1 is independently verified against the now-committed bid - a pure
        // check, exercised for its own sake (see the tests above); the payload is applied one block later.
        SignedExecutionPayloadEnvelope envelope1 = ValidEnvelope(state, bid1.Message!, builderSk, builderIndex: 0);
        Assert.DoesNotThrow(() => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(state, envelope1, new AcceptingNotifier(), new PubkeyCache()));

        // Block 2: its own bid declares bid1's payload delivered (parent_block_hash == bid1.block_hash)
        // with empty parent execution requests, matching the empty root bid1 committed to.
        state.Slot++; // advance one slot inside the same epoch (GloasSlotProcessing would also do this; skipped here to keep the fixture to the two blocks under test)
        SignedExecutionPayloadBid bid2 = SelfBuildBid(state, parentBlockHash: bid1.Message!.BlockHash!, blockHash: Hash(0x99));
        SignedBeaconBlockGloas block2 = MinimalBlock(state, bid2);
        GloasBlockProcessing.ProcessBlock(state, block2.Message!, cache, new PubkeyCache(), new AcceptingNotifier(), spec, verifySignatures: false);

        Hash256 rootAfterBlock2 = SszRoots.HashTreeRoot(state);
        Assert.Multiple(() =>
        {
            Assert.That(state.LatestBlockHash, Is.EqualTo(bid1.Message.BlockHash), "the parent payload must be applied exactly one block later");
            Assert.That(state.BuilderPendingPayments[(int)paymentIndex].Withdrawal!.Amount, Is.EqualTo(0ul), "the settled payment slot must be cleared");
            Assert.That(state.BuilderPendingWithdrawals, Is.Empty, "the settled withdrawal must have been paid out by this same block's own withdrawals step");
            Assert.That(state.Builders![0].Balance, Is.EqualTo(builderStartingBalance - bidValue), "the builder must actually have been paid the bid value");
            Assert.That(rootAfterBlock2, Is.EqualTo(SszRoots.HashTreeRoot(state)), "hash_tree_root must still be stable after the second block");
            Assert.That(rootAfterBlock2, Is.Not.EqualTo(rootAfterBlock1First), "settling and paying out the builder must actually change the state root");
        });
    }

    // ---- Withdrawals computed from state alone ----

    [Test]
    public void ProcessWithdrawals_pays_a_fully_withdrawable_validator_computed_entirely_from_state()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        // Make the parent payload "delivered" so process_withdrawals does not take its early-return path.
        state.LatestBlockHash = state.LatestExecutionPayloadBid!.BlockHash;

        int validatorIndex = 2;
        const ulong startingBalance = 40 * Gwei;
        Validator validator = state.Validators![validatorIndex].Clone();
        validator.WithdrawalCredentials = EthWithdrawalCredentials(0xAB);
        validator.WithdrawableEpoch = 0;
        state.Validators[validatorIndex] = validator;
        state.Balances![validatorIndex] = startingBalance;

        GloasBlockProcessing.ProcessWithdrawals(state);

        Assert.Multiple(() =>
        {
            Assert.That(state.Balances[validatorIndex], Is.EqualTo(0ul), "a fully withdrawable validator's whole balance must be swept");
            Assert.That(state.PayloadExpectedWithdrawals, Has.Length.EqualTo(1));
            Withdrawal withdrawal = state.PayloadExpectedWithdrawals![0];
            Assert.That(withdrawal.ValidatorIndex, Is.EqualTo((ulong)validatorIndex));
            Assert.That(withdrawal.Amount, Is.EqualTo(startingBalance));
            Assert.That(withdrawal.Address, Is.EqualTo(new Address(validator.WithdrawalCredentials.Bytes[12..])));
        });
    }

    [Test]
    public void ProcessWithdrawals_is_a_no_op_when_the_parent_payload_was_not_delivered()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        // LatestBlockHash left at its post-upgrade value, which never equals the committed bid's
        // block hash unless a parent payload was actually applied (see ApplyParentExecutionPayload).
        ulong balanceBefore = state.Balances![2];

        GloasBlockProcessing.ProcessWithdrawals(state);

        Assert.That(state.Balances[2], Is.EqualTo(balanceBefore));
        Assert.That(state.PayloadExpectedWithdrawals, Is.Empty.Or.Null);
    }

    // ---- Declared, by-name gaps must fail loudly rather than silently mishandle the block ----

    [Test]
    public void ProcessOperations_throws_by_name_for_an_attester_slashing_it_does_not_process()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        BeaconBlockBodyGloas body = new()
        {
            Deposits = [],
            AttesterSlashings = [new AttesterSlashingGloas { Attestation1 = new IndexedAttestationGloas(), Attestation2 = new IndexedAttestationGloas() }],
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => GloasBlockProcessing.ProcessOperations(state, body, parentSlot: 0))!;
        Assert.That(ex.Message, Does.Contain("attester slashings"));
    }

    /// <summary>
    /// Without Gloas epoch processing to rotate <c>builder_pending_payments</c> (see
    /// <see cref="GloasBlockProcessing.ApplyParentExecutionPayload"/>'s remarks), this driver
    /// addresses that window purely by slot-in-epoch, so an unsettled payment can only collide with
    /// a later one at the exact same slot-in-epoch - one epoch later - whose own parent payload was
    /// never delivered in between (so nothing ever settled the first one). This test builds exactly
    /// that: block 1 commits a real payment; block 2 (one slot later, self-build, declaring block 1's
    /// payload undelivered) leaves it unsettled; block 3 (one epoch after block 1, same slot-in-epoch)
    /// tries to commit a second payment into the same still-occupied address.
    /// </summary>
    [Test]
    public void ProcessExecutionPayloadBid_throws_by_name_rather_than_silently_destroy_an_unsettled_same_slot_payment()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        const ulong firstBidValue = 4 * Gwei;
        BeaconChainSpec spec = SyntheticSpec();
        EpochCache cache = new();

        // Block 1 (slot 32): the builder's bid is committed - a real, unsettled payment at
        // slot-in-epoch 0 (index SLOTS_PER_EPOCH + 32 % 32 = 32).
        SignedExecutionPayloadBid bid1 = ValidBuilderBid(state, builderSk, builderIndex: 0, value: firstBidValue);
        GloasBlockProcessing.ProcessBlock(state, MinimalBlock(state, bid1).Message!, cache, new PubkeyCache(), new AcceptingNotifier(), spec, verifySignatures: false);

        // Block 2 (slot 33): a self-build bid declaring block 1's payload undelivered (its own
        // parent_block_hash still points at the pre-fork tip, not bid1's block hash), so
        // process_parent_execution_payload takes the empty path and bid1's payment is never settled.
        state.Slot++;
        SignedExecutionPayloadBid bid2 = SelfBuildBid(state, parentBlockHash: state.LatestBlockHash!, blockHash: Hash(0x9A));
        GloasBlockProcessing.ProcessBlock(state, MinimalBlock(state, bid2).Message!, cache, new PubkeyCache(), new AcceptingNotifier(), spec, verifySignatures: false);
        Assert.That(state.BuilderPendingPayments![32].Withdrawal!.Amount, Is.EqualTo(firstBidValue), "bid1's payment must still be sitting, unsettled, at its original address");

        // Block 3 (slot 64 = one epoch after block 1, same slot-in-epoch 0): another real payment
        // from the same builder would overwrite that still-occupied address.
        state.Slot = Presets.SlotsPerEpoch * 2;
        SignedExecutionPayloadBid bid3 = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 6 * Gwei);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid3, spec, new PubkeyCache(), verifySignature: false))!;
        Assert.That(ex.Message, Does.Contain("still holds an unsettled amount"));
    }

    // ---- Fixtures ----

    private static BeaconChainSpec SyntheticSpec() => new()
    {
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 1_606_824_023,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(Bytes.FromHexString("0x06000000"), 0)],
        BlobSchedule = [],
        ElectraForkEpoch = 0,
        FuluForkEpoch = 0,
        MaxBlobsPerBlockElectra = 9,
        GloasForkEpoch = 0,
        GloasForkVersion = GloasVersion,
        Bootnodes = [],
    };

    /// <summary>
    /// A post-upgrade Gloas state at the fork boundary slot, with one active, payload-builder-version
    /// builder (secret key <paramref name="builderSk"/>, index 0, starting balance 40 Gwei returned as
    /// <paramref name="builderStartingBalance"/>) and everyone proposing from validator 0, so a single
    /// derived key signs both blocks and (where exercised) RANDAO.
    /// </summary>
    private static BeaconStateGloas CreateGloasState(out Bls.SecretKey builderSk, out ulong builderStartingBalance)
    {
        BeaconStateFulu pre = CreateFuluState(ValidatorCount);
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        builderSk = DeriveKey(200);
        BlsPublicKey builderPubkey = new(new Bls.P1(builderSk).Compress());
        builderStartingBalance = 40 * Gwei;
        state.Builders = [new Builder
        {
            Pubkey = builderPubkey,
            Version = Presets.PayloadBuilderVersion,
            ExecutionAddress = new Address(BuilderWithdrawalCredentials(0xC0).Bytes[12..]),
            Balance = builderStartingBalance,
            DepositEpoch = 0,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        }];

        return state;
    }

    private static BeaconStateFulu CreateFuluState(int validatorCount)
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

        // Slot 0 through the fork boundary is advanced under the unmodified Fulu pipeline (this
        // exercises the already-covered SlotProcessing/EpochProcessing path, not this file's own code).
        ulong boundarySlot = Presets.SlotsPerEpoch;
        BeaconStateFulu state = new()
        {
            GenesisTime = 1_606_824_023,
            GenesisValidatorsRoot = Hash256.Zero,
            Slot = 0,
            Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = 0, ProposerIndex = 0, ParentRoot = Hash(0x02), StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            BlockRoots = blockRoots,
            StateRoots = stateRoots,
            HistoricalRoots = [],
            Eth1DataVotes = [],
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[validatorCount],
            CurrentEpochParticipation = new byte[validatorCount],
            InactivityScores = new ulong[validatorCount],
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            // Epoch 1 (not 0): the builder onboarded below has DepositEpoch = 0, and is_active_builder
            // requires deposit_epoch strictly less than the finalized checkpoint's epoch.
            FinalizedCheckpoint = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
            JustificationBits = new BitArray(4),
            CurrentSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x60) },
            NextSyncCommittee = new SyncCommittee { Pubkeys = FillCommittee(validators[0].Pubkey), AggregatePubkey = Pubkey(0x61) },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ParentHash = Hash(0x70), BlockHash = Hash(0x71), PrevRandao = Hash(0x72), GasLimit = 30_000_000 },
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
            PendingDeposits = [],
            PendingPartialWithdrawals = [],
            PendingConsolidations = [],
        };

        SlotProcessing.ProcessSlots(state, boundarySlot, new EpochCache());
        return state;
    }

    private static BlsPublicKey[] FillCommittee(BlsPublicKey pubkey)
    {
        BlsPublicKey[] committee = new BlsPublicKey[Presets.SyncCommitteeSize];
        Array.Fill(committee, pubkey);
        return committee;
    }

    /// <summary>A builder-signed bid over <c>DOMAIN_BEACON_BUILDER</c>, valid against <paramref name="state"/> as it stands.</summary>
    private static SignedExecutionPayloadBid ValidBuilderBid(BeaconStateGloas state, Bls.SecretKey builderSk, ulong builderIndex, ulong value)
    {
        ExecutionPayloadBid message = new()
        {
            ParentBlockHash = state.LatestBlockHash,
            ParentBlockRoot = state.GetBlockRootAtSlot(state.Slot - 1),
            BlockHash = Hash(0x88),
            PrevRandao = state.GetRandaoMix(state.GetCurrentEpoch()),
            FeeRecipient = new Address(BuilderWithdrawalCredentials(0xC0).Bytes[12..]),
            GasLimit = 30_000_000,
            BuilderIndex = builderIndex,
            Slot = state.Slot,
            Value = value,
            ExecutionPayment = 0,
            BlobKzgCommitments = [],
            ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas()),
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain);
        BlsSignature signature = new(BlsSigner.Sign(builderSk, signingRoot.Bytes).Bytes);
        return new SignedExecutionPayloadBid { Message = message, Signature = signature };
    }

    /// <summary>A zero-value self-build bid (no builder, no signature to verify) at <paramref name="state"/>'s current slot.</summary>
    private static SignedExecutionPayloadBid SelfBuildBid(BeaconStateGloas state, Hash256 parentBlockHash, Hash256 blockHash) => new()
    {
        Message = new ExecutionPayloadBid
        {
            BuilderIndex = Presets.BuilderIndexSelfBuild,
            ParentBlockHash = parentBlockHash,
            ParentBlockRoot = state.GetBlockRootAtSlot(state.Slot - 1),
            BlockHash = blockHash,
            PrevRandao = state.GetRandaoMix(state.GetCurrentEpoch()),
            FeeRecipient = Address.Zero,
            GasLimit = 30_000_000,
            Slot = state.Slot,
            Value = 0,
            BlobKzgCommitments = [],
            ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas()),
        },
        Signature = new BlsSignature(SignatureSetsG2PointAtInfinity()),
    };

    private static SignedExecutionPayloadEnvelope ValidEnvelope(BeaconStateGloas state, ExecutionPayloadBid bid, Bls.SecretKey builderSk, ulong builderIndex)
    {
        BeaconBlockHeader header = new()
        {
            Slot = state.LatestBlockHeader!.Slot,
            ProposerIndex = state.LatestBlockHeader.ProposerIndex,
            ParentRoot = state.LatestBlockHeader.ParentRoot,
            StateRoot = SszRoots.HashTreeRoot(state),
            BodyRoot = state.LatestBlockHeader.BodyRoot,
        };
        ExecutionPayloadEnvelope message = new()
        {
            Payload = new ExecutionPayloadGloas
            {
                ParentHash = state.LatestBlockHash,
                FeeRecipient = bid.FeeRecipient,
                PrevRandao = bid.PrevRandao,
                GasLimit = bid.GasLimit,
                BlockHash = bid.BlockHash,
                SlotNumber = state.Slot,
                Timestamp = state.ComputeTimeAtSlot(state.Slot),
                Transactions = [],
                Withdrawals = state.PayloadExpectedWithdrawals ?? [],
                ExtraData = [],
                BaseFeePerGas = 0,
                BlockAccessList = [],
            },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = SszRoots.HashTreeRoot(header),
            ParentBeaconBlockRoot = state.LatestBlockHeader.ParentRoot,
        };
        Hash256 domain = state.GetDomain(DomainType.BeaconBuilder);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain);
        BlsSignature signature = new(BlsSigner.Sign(builderSk, signingRoot.Bytes).Bytes);
        return new SignedExecutionPayloadEnvelope { Message = message, Signature = signature };
    }

    /// <summary>A block carrying <paramref name="bid"/> and otherwise-empty operations, at <paramref name="state"/>'s current slot.</summary>
    private static SignedBeaconBlockGloas MinimalBlock(BeaconStateGloas state, SignedExecutionPayloadBid bid) => new()
    {
        Message = new BeaconBlockGloas
        {
            Slot = state.Slot,
            ProposerIndex = state.GetBeaconProposerIndex(),
            ParentRoot = SszRoots.HashTreeRoot(state.LatestBlockHeader!),
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBodyGloas
            {
                RandaoReveal = default,
                Eth1Data = state.Eth1Data,
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize), SyncCommitteeSignature = new BlsSignature(SignatureSetsG2PointAtInfinity()) },
                BlsToExecutionChanges = [],
                SignedExecutionPayloadBid = bid,
                PayloadAttestations = [],
                ParentExecutionRequests = new ExecutionRequestsGloas(),
            },
        },
        Signature = default,
    };

    private static Hash256 EthWithdrawalCredentials(byte fill)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(fill);
        bytes[0] = Presets.EthWithdrawalPrefix;
        return new Hash256(bytes);
    }

    private static Hash256 BuilderWithdrawalCredentials(byte fill)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(fill);
        bytes[0] = Presets.BuilderWithdrawalPrefix;
        return new Hash256(bytes);
    }

    private static Bls.SecretKey DeriveKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)index));

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

    /// <summary>The compressed BLS G2 point at infinity - duplicated as bytes here rather than reaching into <c>Crypto.SignatureSets</c>'s internal constant from a test assembly.</summary>
    private static byte[] SignatureSetsG2PointAtInfinity()
    {
        byte[] bytes = new byte[BlsSignature.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public bool NotifyNewPayload(BeaconBlockBody body) => true;
        public bool NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) => true;
    }
}
