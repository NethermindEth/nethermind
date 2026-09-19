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
        pre.LatestExecutionPayloadHeader = new ExecutionPayloadHeader
        {
            ParentHash = Hash(0x10),
            BlockHash = Hash(0x11),
            PrevRandao = Hash(0x12),
            GasLimit = 36_000_000,
        };
        pre.LatestBlockHeader = new BeaconBlockHeader { Slot = pre.Slot, ProposerIndex = 0, ParentRoot = Hash(0x13), StateRoot = Hash256.Zero, BodyRoot = Hash256.Zero };
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
            Assert.That(bid.Slot, Is.EqualTo(pre.LatestBlockHeader.Slot));
            Assert.That(bid.Value, Is.EqualTo(0ul));
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
            // At least one real (post-lookahead) committee must contain a nonzero, in-range index —
            // otherwise ComputePtc silently produced nothing and this test would not be able to fail.
            Assert.That(post.PtcWindow!.Skip((int)Presets.SlotsPerEpoch).SelectMany(static c => c.Indices!).Any(static idx => idx != 0), Is.True);
        });
    }

    [Test]
    public void UpgradeToGloas_onboards_a_builder_from_a_valid_pending_deposit()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        Bls.SecretKey sk = DeriveKey(100);
        Hash256 withdrawalCredentials = BuilderWithdrawalCredentials(0xAA);
        (BlsPublicKey pubkey, BlsSignature signature) = SignDeposit(sk, withdrawalCredentials, 32 * Gwei);
        pre.PendingDeposits = [new PendingDeposit { Pubkey = pubkey, WithdrawalCredentials = withdrawalCredentials, Amount = 32 * Gwei, Signature = signature, Slot = pre.Slot }];

        BeaconStateGloas post = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());

        Assert.Multiple(() =>
        {
            Assert.That(post.PendingDeposits, Is.Empty, "the deposit that onboarded a builder must leave the pending queue");
            Assert.That(post.Builders, Has.Length.EqualTo(1));
            Assert.That(post.Builders![0].Pubkey, Is.EqualTo(pubkey));
            Assert.That(post.Builders[0].Balance, Is.EqualTo(32 * Gwei));
            Assert.That(post.Builders[0].ExecutionAddress, Is.EqualTo(new Address(withdrawalCredentials.Bytes[12..])));
            Assert.That(post.Builders[0].WithdrawableEpoch, Is.EqualTo(Presets.FarFutureEpoch));
        });
    }

    [Test]
    public void UpgradeToGloas_leaves_a_pending_deposit_for_an_existing_validator_in_the_queue()
    {
        BeaconStateFulu pre = CreateState(validatorCount: ValidatorCount);
        BlsPublicKey existingValidatorPubkey = pre.Validators![0].Pubkey;
        pre.PendingDeposits = [new PendingDeposit { Pubkey = existingValidatorPubkey, WithdrawalCredentials = Hash256.Zero, Amount = 1 * Gwei, Signature = default, Slot = pre.Slot }];

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
