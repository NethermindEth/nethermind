// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.StateTransition;

public class GloasForkTransitionTests
{
    private const ulong Gwei = 1_000_000_000;
    // Large enough that every (slot, committee) slice in a 32-slot epoch gets at least one member;
    // with too few validators most slices are empty and ComputeBalanceWeightedSelection has nothing
    // to sample from.
    private const int ValidatorCount = 2048;
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");
    private static readonly byte[] GloasVersion = Bytes.FromHexString("0x07000000");

    [Test]
    public void UpgradeToGloas_preserves_the_fields_the_spec_says_carry_over()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        // Distinctive non-zero values: every one of these is asserted by value below, and a fixture
        // that leaves them at their default makes those assertions pass against a dropped field.
        pre.Slot = 64;
        pre.Eth1DepositIndex = 7;
        pre.NextWithdrawalIndex = 11;
        pre.NextWithdrawalValidatorIndex = 13;
        pre.DepositRequestsStartIndex = 17;
        BeaconChainSpec spec = SyntheticSpec();

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, spec);

        Assert.Multiple(() =>
        {
            Assert.That(post.GenesisTime, Is.EqualTo(pre.GenesisTime));
            Assert.That(post.GenesisValidatorsRoot, Is.EqualTo(pre.GenesisValidatorsRoot));
            Assert.That(post.Slot, Is.EqualTo(pre.Slot));
            Assert.That(post.LatestBlockHeader, Is.SameAs(pre.LatestBlockHeader));
            Assert.That(post.BlockRoots, Is.SameAs(pre.BlockRoots));
            Assert.That(post.StateRoots, Is.SameAs(pre.StateRoots));
            Assert.That(post.Eth1Data, Is.SameAs(pre.Eth1Data));
            Assert.That(post.Eth1DepositIndex, Is.EqualTo(pre.Eth1DepositIndex));
            Assert.That(post.Validators, Is.SameAs(pre.Validators));
            Assert.That(post.Balances, Is.SameAs(pre.Balances));
            Assert.That(post.RandaoMixes, Is.SameAs(pre.RandaoMixes));
            Assert.That(post.Slashings, Is.SameAs(pre.Slashings));
            Assert.That(post.JustificationBits, Is.SameAs(pre.JustificationBits));
            Assert.That(post.PreviousJustifiedCheckpoint, Is.SameAs(pre.PreviousJustifiedCheckpoint));
            Assert.That(post.CurrentJustifiedCheckpoint, Is.SameAs(pre.CurrentJustifiedCheckpoint));
            Assert.That(post.FinalizedCheckpoint, Is.SameAs(pre.FinalizedCheckpoint));
            Assert.That(post.InactivityScores, Is.SameAs(pre.InactivityScores));
            Assert.That(post.CurrentSyncCommittee, Is.SameAs(pre.CurrentSyncCommittee));
            Assert.That(post.NextSyncCommittee, Is.SameAs(pre.NextSyncCommittee));
            Assert.That(post.NextWithdrawalIndex, Is.EqualTo(pre.NextWithdrawalIndex));
            Assert.That(post.NextWithdrawalValidatorIndex, Is.EqualTo(pre.NextWithdrawalValidatorIndex));
            Assert.That(post.PendingPartialWithdrawals, Is.SameAs(pre.PendingPartialWithdrawals));
            Assert.That(post.PendingConsolidations, Is.SameAs(pre.PendingConsolidations));
            Assert.That(post.ProposerLookahead, Is.SameAs(pre.ProposerLookahead));
            Assert.That(post.DepositRequestsStartIndex, Is.EqualTo(pre.DepositRequestsStartIndex));
            Assert.That(post.EarliestExitEpoch, Is.EqualTo(pre.EarliestExitEpoch));
            Assert.That(post.EarliestConsolidationEpoch, Is.EqualTo(pre.EarliestConsolidationEpoch));
        });
    }

    [Test]
    public void UpgradeToGloas_sets_the_fork_to_the_gloas_version_at_the_current_epoch()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        pre.Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 };
        BeaconChainSpec spec = SyntheticSpec();

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, spec);

        Assert.Multiple(() =>
        {
            Assert.That(post.Fork!.PreviousVersion, Is.EqualTo(pre.Fork.CurrentVersion));
            Assert.That(post.Fork.CurrentVersion, Is.EqualTo(spec.GloasForkVersion));
            Assert.That(post.Fork.Epoch, Is.EqualTo(pre.GetCurrentEpoch()));
        });
    }

    [Test]
    public void UpgradeToGloas_initialises_the_new_fields_to_the_specs_values()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        // Every bid source differs from every other candidate, so a bid field read from the wrong one
        // fails: the last block (slot 62) precedes the fork-boundary state slot 64 across missed slots.
        pre.Slot = 64;
        pre.LatestExecutionPayloadHeader = new ExecutionPayloadHeader
        {
            ParentHash = Hash(0x10),
            FeeRecipient = new Address(Hash(0x14).Bytes[12..]),
            StateRoot = Hash(0x15),
            ReceiptsRoot = Hash(0x16),
            BlockHash = Hash(0x11),
            PrevRandao = Hash(0x12),
            BlockNumber = 61,
            GasLimit = 36_000_000,
            GasUsed = 21_000_000,
        };
        pre.LatestBlockHeader = new BeaconBlockHeader { Slot = 62, ProposerIndex = 3, ParentRoot = Hash(0x13), StateRoot = Hash(0x17), BodyRoot = Hash(0x18) };
        BeaconChainSpec spec = SyntheticSpec();

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, spec);

        Assert.Multiple(() =>
        {
            Assert.That(post.LatestBlockHash, Is.EqualTo(pre.LatestExecutionPayloadHeader.BlockHash));
            Assert.That(post.Builders, Is.Empty);
            Assert.That(post.NextWithdrawalBuilderIndex, Is.EqualTo(0ul));
            Assert.That(post.BuilderPendingWithdrawals, Is.Empty);
            Assert.That(post.PayloadExpectedWithdrawals, Is.Empty);
            Assert.That(post.BuilderPendingPayments, Has.Length.EqualTo((int)Presets.BuilderPendingPaymentsLength));
            Assert.That(post.BuilderPendingPayments!.All(static p => p.Weight == 0 && p.ProposerIndex == 0), Is.True);
            Assert.That(post.PtcWindow, Has.Length.EqualTo((int)Presets.PtcWindowLength));

            BitArrayAllTrue(post.ExecutionPayloadAvailability!, (int)Presets.SlotsPerHistoricalRoot);

            ExecutionPayloadBid bid = post.LatestExecutionPayloadBid!;
            Assert.That(bid.ParentBlockHash, Is.EqualTo(pre.LatestExecutionPayloadHeader.ParentHash));
            Assert.That(bid.ParentBlockRoot, Is.EqualTo(pre.LatestBlockHeader.ParentRoot));
            Assert.That(bid.BlockHash, Is.EqualTo(pre.LatestExecutionPayloadHeader.BlockHash));
            Assert.That(bid.PrevRandao, Is.EqualTo(pre.LatestExecutionPayloadHeader.PrevRandao));
            Assert.That(bid.GasLimit, Is.EqualTo(pre.LatestExecutionPayloadHeader.GasLimit));
            Assert.That(bid.BuilderIndex, Is.EqualTo(Presets.BuilderIndexSelfBuild));
            Assert.That(bid.FeeRecipient, Is.EqualTo(Address.Zero));
            Assert.That(bid.Slot, Is.EqualTo(62ul), "the bid's slot is latest_block_header.slot, not the state slot");
            Assert.That(bid.Value, Is.EqualTo(0ul));
            Assert.That(bid.ExecutionPayment, Is.EqualTo(0ul));
            Assert.That(bid.BlobKzgCommitments, Is.Empty);
        });
    }

    [Test]
    public void UpgradeToGloas_initialises_a_ptc_window_of_the_spec_length_with_an_all_zero_previous_epoch()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        BeaconChainSpec spec = SyntheticSpec();

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, spec);

        Assert.Multiple(() =>
        {
            Assert.That(post.PtcWindow, Has.Length.EqualTo((int)Presets.PtcWindowLength));
            for (int i = 0; i < (int)Presets.SlotsPerEpoch; i++)
            {
                // A null Indices array is the SSZ null-defaults-to-zero-vector convention (see
                // GloasForkTransition.EmptyPtcWindow), not a missing value.
                Assert.That((post.PtcWindow![i].Indices ?? []).All(static idx => idx == 0), Is.True,
                    $"empty-previous-epoch committee {i} must be all-zero, matching the spec's placeholder history");
            }
            // At least one real (post-lookahead) committee must contain a nonzero, in-range index -
            // otherwise ComputePtc silently produced nothing and this test would not be able to fail.
            Assert.That(post.PtcWindow!.Skip((int)Presets.SlotsPerEpoch).SelectMany(static c => c.Indices!).Any(static idx => idx != 0), Is.True);
        });
    }

    // initialize_ptc_window's empty previous epoch is explicit zero vectors (specs/gloas/fork.md); the
    // upgrade leaves those entries' Indices null, so root and encoding must not tell the two apart.
    [Test]
    public void A_ptc_with_null_indices_hashes_and_encodes_as_an_explicit_zero_vector()
    {
        PayloadTimelinessCommittee nullCommittee = new();
        PayloadTimelinessCommittee zeroCommittee = new() { Indices = new ulong[(int)Presets.PtcSize] };
        PayloadTimelinessCommittee[] nullWindow = [.. Enumerable.Range(0, (int)Presets.PtcWindowLength).Select(static _ => new PayloadTimelinessCommittee())];
        PayloadTimelinessCommittee[] zeroWindow = [.. Enumerable.Range(0, (int)Presets.PtcWindowLength).Select(static _ => new PayloadTimelinessCommittee { Indices = new ulong[(int)Presets.PtcSize] })];

        PayloadTimelinessCommittee.MerkleizeVector(nullWindow, out UInt256 nullWindowRoot);
        PayloadTimelinessCommittee.MerkleizeVector(zeroWindow, out UInt256 zeroWindowRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SszRoots.HashTreeRoot(nullCommittee), Is.EqualTo(SszRoots.HashTreeRoot(zeroCommittee)));
            Assert.That(PayloadTimelinessCommittee.Encode(nullCommittee), Is.EqualTo(PayloadTimelinessCommittee.Encode(zeroCommittee)));
            Assert.That(nullWindowRoot, Is.EqualTo(zeroWindowRoot));
            Assert.That(PayloadTimelinessCommittee.Encode(nullWindow), Is.EqualTo(PayloadTimelinessCommittee.Encode(zeroWindow)));
        }
    }

    // get_index_for_new_builder (specs/gloas/beacon-chain.md): a slot is reusable from its withdrawable
    // epoch on, and only once drained; otherwise the new builder is appended after the active one.
    [TestCase(5ul, 0ul, 0ul, TestName = "withdrawable_this_epoch_and_drained_is_reused")]
    [TestCase(4ul, 0ul, 0ul, TestName = "withdrawable_before_this_epoch_and_drained_is_reused")]
    [TestCase(6ul, 0ul, 2ul, TestName = "withdrawable_next_epoch_is_not_reused")]
    [TestCase(5ul, 1ul, 2ul, TestName = "withdrawable_but_not_drained_is_not_reused")]
    public void GetIndexForNewBuilder_reuses_only_a_withdrawable_drained_slot(ulong withdrawableEpoch, ulong balance, ulong expectedIndex)
    {
        const ulong currentEpoch = 5;
        Builder[] builders =
        [
            new() { WithdrawableEpoch = withdrawableEpoch, Balance = balance },
            new() { WithdrawableEpoch = Presets.FarFutureEpoch, Balance = 32 * Gwei },
        ];

        Assert.That(GloasForkTransition.GetIndexForNewBuilder(builders, currentEpoch), Is.EqualTo(expectedIndex));
    }

    [Test]
    public void UpgradeToGloas_onboards_a_builder_from_a_valid_pending_deposit()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        // The deposit (slot 40, epoch 1) predates the fork-boundary state (slot 64, epoch 2).
        pre.Slot = 64;
        Bls.SecretKey sk = DeriveKey(100);
        Hash256 withdrawalCredentials = BuilderWithdrawalCredentials(0xAA);
        (BlsPublicKey pubkey, BlsSignature signature) = SignDeposit(sk, withdrawalCredentials, 32 * Gwei);
        pre.PendingDeposits = [new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = 32 * Gwei, Signature = signature, Slot = 40 }];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.PendingDeposits, Is.Empty, "the deposit that onboarded a builder must leave the pending queue");
            Assert.That(post.Builders, Has.Length.EqualTo(1));
            Assert.That(post.Builders![0].Pubkey, Is.EqualTo(pubkey));
            Assert.That(post.Builders[0].Balance, Is.EqualTo(32 * Gwei));
            Assert.That(post.Builders[0].ExecutionAddress, Is.EqualTo(new Address(withdrawalCredentials.Bytes[12..])));
            Assert.That(post.Builders[0].WithdrawableEpoch, Is.EqualTo(Presets.FarFutureEpoch));
            Assert.That(post.Builders[0].DepositEpoch, Is.EqualTo(1ul), "add_builder_to_registry takes the deposit's epoch, not the state's");
        });
    }

    // onboard_builders_from_pending_deposits (specs/gloas/fork.md): a later deposit for a builder already
    // onboarded from this queue tops up its balance, with no signature check, and leaves the queue.
    [Test]
    public void UpgradeToGloas_tops_up_a_builder_onboarded_earlier_in_the_same_queue()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        Bls.SecretKey sk = DeriveKey(101);
        Hash256 withdrawalCredentials = BuilderWithdrawalCredentials(0xAB);
        (BlsPublicKey pubkey, BlsSignature signature) = SignDeposit(sk, withdrawalCredentials, 32 * Gwei);
        pre.PendingDeposits =
        [
            new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = 32 * Gwei, Signature = signature, Slot = pre.Slot },
            new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = 5 * Gwei, Signature = Signature(0xDE), Slot = pre.Slot },
        ];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.PendingDeposits, Is.Empty);
            Assert.That(post.Builders, Has.Length.EqualTo(1));
            Assert.That(post.Builders![0].Balance, Is.EqualTo(37 * Gwei));
        });
    }

    // A validly signed builder-credential deposit would onboard a builder, so only the existing-validator
    // check of onboard_builders_from_pending_deposits (specs/gloas/fork.md) keeps it in the queue.
    [Test]
    public void UpgradeToGloas_leaves_a_pending_deposit_for_an_existing_validator_in_the_queue()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        Hash256 withdrawalCredentials = BuilderWithdrawalCredentials(0xAC);
        (BlsPublicKey existingValidatorPubkey, BlsSignature signature) = SignDeposit(DeriveKey(102), withdrawalCredentials, 32 * Gwei);
        pre.Validators![0].Pubkey = existingValidatorPubkey;
        pre.PendingDeposits = [new PendingDeposit { Pubkey = existingValidatorPubkey, WithdrawalCredentials = withdrawalCredentials, Amount = 32 * Gwei, Signature = signature, Slot = pre.Slot }];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.Builders, Is.Empty);
            Assert.That(post.PendingDeposits, Has.Length.EqualTo(1));
            Assert.That(post.PendingDeposits![0].Pubkey, Is.EqualTo(existingValidatorPubkey));
        });
    }

    [Test]
    public void UpgradeToGloas_drops_a_builder_prefixed_deposit_with_an_invalid_signature()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        Hash256 withdrawalCredentials = BuilderWithdrawalCredentials(0xBB);
        pre.PendingDeposits = [new PendingDeposit { Pubkey = Pubkey(0xCC), WithdrawalCredentials = withdrawalCredentials, Amount = 32 * Gwei, Signature = Signature(0xDD), Slot = pre.Slot }];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.Builders, Is.Empty, "an invalid-signature deposit must not create a builder");
            Assert.That(post.PendingDeposits, Is.Empty, "the spec drops it outright rather than re-queuing it");
        });
    }

    [Test]
    public void UpgradeToGloas_keeps_a_non_builder_prefixed_deposit_for_a_new_pubkey_in_the_queue()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        pre.PendingDeposits = [new PendingDeposit { Pubkey = Pubkey(0xEE), WithdrawalCredentials = Hash(0x00), Amount = 32 * Gwei, Signature = Signature(0xEF), Slot = pre.Slot }];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.Builders, Is.Empty);
            Assert.That(post.PendingDeposits, Has.Length.EqualTo(1), "a 0x00/0x01/0x02-prefixed deposit for an unknown pubkey is a future-validator deposit, not a builder one");
        });
    }

    private static void BitArrayAllTrue(System.Collections.BitArray bits, int length)
    {
        Assert.That(bits.Length, Is.EqualTo(length));
        for (int i = 0; i < length; i++)
            Assert.That(bits[i], Is.True, $"bit {i} must be set: every historical slot starts marked payload-delivered");
    }

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

    private static BeaconStateFulu CreateState(int validatorCount)
    {
        Hash256[] randaoMixes = new Hash256[(int)Presets.EpochsPerHistoricalVector];
        System.Array.Fill(randaoMixes, Hash(0x42));

        Validator[] validators = new Validator[validatorCount];
        ulong[] balances = new ulong[validatorCount];
        for (int i = 0; i < validatorCount; i++)
        {
            validators[i] = new Validator
            {
                Pubkey = PubkeyForIndex(i),
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
            GenesisTime = 1_606_824_023,
            GenesisValidatorsRoot = Hash(0x01),
            Slot = 0,
            Fork = new Fork { PreviousVersion = Bytes.FromHexString("0x05000000"), CurrentVersion = Bytes.FromHexString("0x06000000"), Epoch = 0 },
            LatestBlockHeader = new BeaconBlockHeader { Slot = 0, ProposerIndex = 0, ParentRoot = Hash(0x02), StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero },
            Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
            Validators = validators,
            Balances = balances,
            RandaoMixes = randaoMixes,
            Slashings = new ulong[(int)Presets.EpochsPerSlashingsVector],
            PreviousEpochParticipation = new byte[validatorCount],
            CurrentEpochParticipation = new byte[validatorCount],
            InactivityScores = new ulong[validatorCount],
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            FinalizedCheckpoint = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            CurrentSyncCommittee = new SyncCommittee { AggregatePubkey = Pubkey(0x60) },
            NextSyncCommittee = new SyncCommittee { AggregatePubkey = Pubkey(0x61) },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ParentHash = Hash(0x70), BlockHash = Hash(0x71), PrevRandao = Hash(0x72), GasLimit = 30_000_000 },
            ProposerLookahead = new ulong[(int)Presets.ProposerLookaheadSlots],
        };
    }

    private static Hash256 BuilderWithdrawalCredentials(byte fill)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(fill);
        bytes[0] = Presets.BuilderWithdrawalPrefix;
        return new Hash256(bytes);
    }

    private static Bls.SecretKey DeriveKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), unchecked((uint)index));

    private static (BlsPublicKey Pubkey, BlsSignature Signature) SignDeposit(Bls.SecretKey sk, Hash256 withdrawalCredentials, ulong amount)
    {
        BlsPublicKey pubkey = new(new Bls.P1(sk).Compress());
        DepositMessage.Merkleize(new DepositMessage { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, Presets.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
        BlsSignature signature = new(BlsSigner.Sign(sk, signingRoot.Bytes).Bytes);
        return (pubkey, signature);
    }

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

    /// <summary>A distinct pubkey per validator index, unlike <see cref="Pubkey"/>'s single repeated fill byte.</summary>
    private static BlsPublicKey PubkeyForIndex(int index)
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes.AsSpan().Fill(0x50);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(BlsPublicKey.Length - 4), index);
        return new BlsPublicKey(bytes);
    }

    private static BlsSignature Signature(byte value)
    {
        byte[] bytes = new byte[BlsSignature.Length];
        bytes.AsSpan().Fill(value);
        return new BlsSignature(bytes);
    }
}
