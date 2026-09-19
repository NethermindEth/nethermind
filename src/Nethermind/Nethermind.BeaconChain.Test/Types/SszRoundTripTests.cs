// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using Transaction = Nethermind.BeaconChain.Types.Transaction;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Types;

public class SszRoundTripTests
{
    [Test]
    public void SignedBeaconBlock_round_trips_with_stable_root()
    {
        SignedBeaconBlock original = CreateRepresentativeBlock();

        byte[] encoded = SignedBeaconBlock.Encode(original);
        SignedBeaconBlock.Decode(encoded, out SignedBeaconBlock decoded);
        byte[] reEncoded = SignedBeaconBlock.Encode(decoded);
        SignedBeaconBlock.Merkleize(original, out UInt256 originalRoot);
        SignedBeaconBlock.Merkleize(decoded, out UInt256 decodedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(reEncoded, Is.EqualTo(encoded));
            Assert.That(decodedRoot, Is.EqualTo(originalRoot));
            Assert.That(originalRoot, Is.Not.EqualTo(UInt256.Zero));
            Assert.That(decoded.Message!.Body!.ExecutionPayload!.Transactions, Has.Length.EqualTo(2));
            Assert.That(decoded.Message.Body.ExecutionPayload.Withdrawals, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void BeaconStateFulu_round_trips_and_diverges_from_equivalent_electra_state()
    {
        BeaconStateFulu original = CreateSyntheticState<BeaconStateFulu>();
        original.ProposerLookahead = [.. Enumerable.Range(0, 64).Select(static i => (ulong)(i % 4))];

        byte[] encoded = BeaconStateFulu.Encode(original);
        BeaconStateFulu.Decode(encoded, out BeaconStateFulu decoded);
        byte[] reEncoded = BeaconStateFulu.Encode(decoded);
        BeaconStateFulu.Merkleize(original, out UInt256 fuluRoot);
        BeaconStateFulu.Merkleize(decoded, out UInt256 decodedRoot);
        BeaconStateElectra.Merkleize(CreateSyntheticState<BeaconStateElectra>(), out UInt256 electraRoot);

        Assert.Multiple(() =>
        {
            Assert.That(reEncoded, Is.EqualTo(encoded));
            Assert.That(decodedRoot, Is.EqualTo(fuluRoot));
            Assert.That(fuluRoot, Is.Not.EqualTo(electraRoot), "proposer_lookahead must be mixed into the Fulu root");
            Assert.That(decoded.Validators, Has.Length.EqualTo(4));
            Assert.That(decoded.ProposerLookahead, Is.EqualTo(original.ProposerLookahead));
        });
    }

    [Test]
    public void Zeroed_checkpoint_hash_tree_root_matches_spec_value()
    {
        Checkpoint.Merkleize(new Checkpoint(), out UInt256 root);

        // HTR of {epoch: 0, root: 0x00..00} = sha256(32 zero bytes || 32 zero bytes)
        UInt256 expected = new(Bytes.FromHexString("0xf5a5fd42d16a20302798ef6ed309979b43003d2320d9f0e8ea9831a92759fb4b"));
        Assert.That(root, Is.EqualTo(expected));
    }

    private static SignedBeaconBlock CreateRepresentativeBlock()
    {
        BitArray aggregationBits = new(200);
        aggregationBits.Set(0, true);
        aggregationBits.Set(63, true);
        aggregationBits.Set(199, true);

        BitArray committeeBits = new(64);
        committeeBits.Set(3, true);

        Attestation attestation = new()
        {
            AggregationBits = aggregationBits,
            Data = new AttestationData
            {
                Slot = 1234,
                Index = 3,
                BeaconBlockRoot = Hash(0x01),
                Source = new Checkpoint { Epoch = 37, Root = Hash(0x02) },
                Target = new Checkpoint { Epoch = 38, Root = Hash(0x03) },
            },
            Signature = Signature(0x10),
            CommitteeBits = committeeBits,
        };

        Deposit deposit = new()
        {
            Proof = [.. Enumerable.Range(0, 33).Select(static i => Hash((byte)(0x20 + i)))],
            Data = new DepositData
            {
                Pubkey = Pubkey(0x0A),
                WithdrawalCredentials = Hash(0x0B),
                Amount = 32_000_000_000,
                Signature = Signature(0x0C),
            },
        };

        BitArray syncBits = new(512);
        for (int i = 0; i < 512; i += 7)
        {
            syncBits.Set(i, true);
        }

        ExecutionPayload payload = new()
        {
            ParentHash = Hash(0x40),
            FeeRecipient = new Address(Filled(Address.Size, 0x41)),
            StateRoot = Hash(0x42),
            ReceiptsRoot = Hash(0x43),
            LogsBloom = new Bloom(Filled(Bloom.ByteLength, 0x44)),
            PrevRandao = Hash(0x45),
            BlockNumber = 9_000_001,
            GasLimit = 30_000_000,
            GasUsed = 21_000,
            Timestamp = 1_750_000_000,
            ExtraData = Bytes.FromHexString("0xc0ffee"),
            BaseFeePerGas = 7,
            BlockHash = Hash(0x46),
            Transactions =
            [
                new Transaction { Bytes = Bytes.FromHexString("0x02f87001020304") },
                new Transaction { Bytes = Bytes.FromHexString("0xdeadbeef") },
            ],
            Withdrawals =
            [
                new Withdrawal { Index = 5, ValidatorIndex = 9, Address = new Address(Filled(Address.Size, 0x47)), Amount = 1_000_000 },
            ],
            BlobGasUsed = 131_072,
            ExcessBlobGas = 0,
        };

        BeaconBlockBody body = new()
        {
            RandaoReveal = Signature(0x50),
            Eth1Data = new Eth1Data { DepositRoot = Hash(0x51), DepositCount = 42, BlockHash = Hash(0x52) },
            Graffiti = Hash(0x53),
            ProposerSlashings = [],
            AttesterSlashings = [],
            Attestations = [attestation],
            Deposits = [deposit],
            VoluntaryExits = [],
            SyncAggregate = new SyncAggregate { SyncCommitteeBits = syncBits, SyncCommitteeSignature = Signature(0x54) },
            ExecutionPayload = payload,
            BlsToExecutionChanges = [],
            BlobKzgCommitments = [SszKzgCommitment.FromSpan(Filled(SszKzgCommitment.KzgCommitmentLength, 0x55))],
            ExecutionRequests = new ExecutionRequests
            {
                Deposits =
                [
                    new DepositRequest
                    {
                        Pubkey = Pubkey(0x60),
                        WithdrawalCredentials = Hash(0x61),
                        Amount = 1_000_000_000,
                        Signature = Signature(0x62),
                        Index = 7,
                    },
                ],
                Withdrawals = [],
                Consolidations = [],
            },
        };

        return new SignedBeaconBlock
        {
            Message = new BeaconBlock
            {
                Slot = 123_456,
                ProposerIndex = 21,
                ParentRoot = Hash(0x70),
                StateRoot = Hash(0x71),
                Body = body,
            },
            Signature = Signature(0x72),
        };
    }

    private static T CreateSyntheticState<T>() where T : BeaconStateElectra, new()
    {
        Hash256[] randaoMixes = new Hash256[65_536];
        Array.Fill(randaoMixes, Hash256.Zero);
        randaoMixes[0] = Hash(0x80);
        randaoMixes[65_535] = Hash(0x81);

        BitArray justificationBits = new(4);
        justificationBits.Set(0, true);
        justificationBits.Set(1, true);

        BlsPublicKey[] syncPubkeys = new BlsPublicKey[512];
        syncPubkeys[0] = Pubkey(0x90);
        syncPubkeys[511] = Pubkey(0x91);

        Validator[] validators = [.. Enumerable.Range(0, 4).Select(static i => new Validator
        {
            Pubkey = Pubkey((byte)(0xA0 + i)),
            WithdrawalCredentials = Hash((byte)(0xB0 + i)),
            EffectiveBalance = 32_000_000_000,
            Slashed = i == 3,
            ActivationEligibilityEpoch = 0,
            ActivationEpoch = 0,
            ExitEpoch = ulong.MaxValue,
            WithdrawableEpoch = ulong.MaxValue,
        })];

        return new T
        {
            GenesisTime = 1_606_824_023,
            GenesisValidatorsRoot = Hash(0xC0),
            Slot = 11_649_024,
            Fork = new Fork
            {
                PreviousVersion = Bytes.FromHexString("0x05000000"),
                CurrentVersion = Bytes.FromHexString("0x06000000"),
                Epoch = 364_032,
            },
            LatestBlockHeader = new BeaconBlockHeader
            {
                Slot = 11_649_023,
                ProposerIndex = 1,
                ParentRoot = Hash(0xC1),
                StateRoot = Hash(0xC2),
                BodyRoot = Hash(0xC3),
            },
            Eth1Data = new Eth1Data { DepositRoot = Hash(0xC4), DepositCount = 4, BlockHash = Hash(0xC5) },
            Eth1DepositIndex = 4,
            Validators = validators,
            Balances = [32_000_000_000, 32_000_000_000, 31_000_000_000, 0],
            RandaoMixes = randaoMixes,
            PreviousEpochParticipation = [0x07, 0x07, 0x01, 0x00],
            CurrentEpochParticipation = [0x07, 0x03, 0x00, 0x00],
            JustificationBits = justificationBits,
            PreviousJustifiedCheckpoint = new Checkpoint { Epoch = 364_030, Root = Hash(0xC6) },
            CurrentJustifiedCheckpoint = new Checkpoint { Epoch = 364_031, Root = Hash(0xC7) },
            FinalizedCheckpoint = new Checkpoint { Epoch = 364_030, Root = Hash(0xC8) },
            InactivityScores = [0, 0, 1, 5],
            CurrentSyncCommittee = new SyncCommittee { Pubkeys = syncPubkeys, AggregatePubkey = Pubkey(0x92) },
            NextSyncCommittee = new SyncCommittee { Pubkeys = syncPubkeys, AggregatePubkey = Pubkey(0x93) },
            LatestExecutionPayloadHeader = new ExecutionPayloadHeader
            {
                ParentHash = Hash(0xD0),
                FeeRecipient = new Address(Filled(Address.Size, 0xD1)),
                StateRoot = Hash(0xD2),
                ReceiptsRoot = Hash(0xD3),
                LogsBloom = new Bloom(Filled(Bloom.ByteLength, 0xD4)),
                PrevRandao = Hash(0xD5),
                BlockNumber = 22_000_000,
                GasLimit = 30_000_000,
                GasUsed = 15_000_000,
                Timestamp = 1_750_000_000,
                ExtraData = Bytes.FromHexString("0x4e65746865726d696e64"),
                BaseFeePerGas = 1_000_000_000,
                BlockHash = Hash(0xD6),
                TransactionsRoot = Hash(0xD7),
                WithdrawalsRoot = Hash(0xD8),
                BlobGasUsed = 131_072,
                ExcessBlobGas = 393_216,
            },
            NextWithdrawalIndex = 99,
            NextWithdrawalValidatorIndex = 2,
            DepositRequestsStartIndex = ulong.MaxValue,
            EarliestExitEpoch = 364_033,
            EarliestConsolidationEpoch = 364_033,
            PendingDeposits =
            [
                new PendingDeposit
                {
                    Pubkey = Pubkey(0xE0),
                    WithdrawalCredentials = Hash(0xE1),
                    Amount = 1_000_000_000,
                    Signature = Signature(0xE2),
                    Slot = 11_649_000,
                },
            ],
        };
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static Hash256 Hash(byte value) => new(Filled(Hash256.Size, value));

    private static BlsPublicKey Pubkey(byte value) => new(Filled(BlsPublicKey.Length, value));

    private static BlsSignature Signature(byte value) => new(Filled(BlsSignature.Length, value));
}
