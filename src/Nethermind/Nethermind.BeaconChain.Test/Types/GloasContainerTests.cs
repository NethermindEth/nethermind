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
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Types;

/// <summary>
/// SSZ round trip and hash-tree-root coverage for the Gloas containers in <c>GloasContainers.cs</c>
/// and <c>BeaconStateGloas</c>.
/// </summary>
/// <remarks>
/// Expected roots for the seven plain (non-progressive) container types below, and for the all-default
/// <see cref="ExecutionPayloadBid"/> and <see cref="BeaconStateGloas"/> values, were computed
/// independently with ethereum/ssz-specs (`pip install git+https://github.com/ethereum/ssz-specs.git`,
/// installed 2026-09-19; package version 0.1.0 declared in its own pyproject.toml) — the same reference
/// SSZ implementation the consensus-specs test-vector generator itself now uses (see that repo's
/// tooling-swap note in this task's Gloas survey). Field types and order there were copied from the
/// spec class definitions cited on each type in GloasContainers.cs / BeaconState.cs, not from this
/// codebase's own encoder, so a mismatch here would be a genuine divergence, not a tautology. The
/// scripts used are not checked in; re-derive them from the field lists cited in the type docs if this
/// ever needs re-verifying.
/// <para/>
/// <see cref="ExecutionPayloadEnvelope"/>, <see cref="SignedExecutionPayloadEnvelope"/>,
/// <see cref="PayloadAttestation"/>, <see cref="IndexedPayloadAttestation"/>,
/// <see cref="SignedExecutionPayloadBid"/> and <see cref="BeaconBlockBodyGloas"/> get round-trip
/// coverage only (decode(encode(x)) reproduces x's own root) — see 'unresolved' in the task report for
/// why an independent root was not attempted for every progressive container.
/// </remarks>
public class GloasContainerTests
{
    [Test]
    public void Builder_hash_tree_root_matches_an_independently_computed_value()
    {
        Builder builder = new()
        {
            Pubkey = Pubkey(0xA0),
            Version = 0,
            ExecutionAddress = new Address(Filled(Address.Size, 0xA1)),
            Balance = 32_000_000_000,
            DepositEpoch = 500_000,
            WithdrawableEpoch = ulong.MaxValue,
        };

        AssertRoundTripsAndMatchesRoot(builder, "0x85b4caa9007afd4605ad383f45294c3efb78ae4ce35348da60ead199ae166463");
    }

    [Test]
    public void BuilderPendingWithdrawal_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingWithdrawal withdrawal = new()
        {
            FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
            Amount = 1_000_000_000,
            BuilderIndex = 7,
        };

        AssertRoundTripsAndMatchesRoot(withdrawal, "0x807a2c0b918f9fdeaa0d8f84cc7b87aa441c8091c7b07abe50f81fc0a99bfbd6");
    }

    [Test]
    public void BuilderPendingPayment_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingPayment payment = new()
        {
            Weight = 123_456,
            Withdrawal = new BuilderPendingWithdrawal
            {
                FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
                Amount = 1_000_000_000,
                BuilderIndex = 7,
            },
            ProposerIndex = 42,
        };

        AssertRoundTripsAndMatchesRoot(payment, "0xfea1afeaba53f8ec3224a442a92ff2963fca55f27377fb0d3bfbc4d876c09469");
    }

    [Test]
    public void BuilderDepositRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderDepositRequest request = new()
        {
            Pubkey = Pubkey(0xC0),
            WithdrawalCredentials = Hash(0xC1),
            Amount = 32_000_000_000,
            Signature = Signature(0xC2),
        };

        AssertRoundTripsAndMatchesRoot(request, "0x9583b5073b93f6c43168ee4d30fb805338b6616184f318d9b5acd291ccccb4e5");
    }

    [Test]
    public void BuilderExitRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderExitRequest request = new()
        {
            SourceAddress = new Address(Filled(Address.Size, 0xD0)),
            Pubkey = Pubkey(0xD1),
        };

        AssertRoundTripsAndMatchesRoot(request, "0xff55245250859ea9c1cc7a15a196a9bc37b1ee348fb71b6ae1c51d40eaa1feb0");
    }

    [Test]
    public void PayloadAttestationData_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationData data = new()
        {
            BeaconBlockRoot = Hash(0xE0),
            Slot = 11_649_024,
            PayloadPresent = true,
            BlobDataAvailable = false,
        };

        AssertRoundTripsAndMatchesRoot(data, "0xfadac1fabeb7d8313dc7cd07feb19348bda5a2d662454db338c7043cdc4e5226");
    }

    [Test]
    public void PayloadAttestationMessage_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationMessage message = new()
        {
            ValidatorIndex = 17,
            Data = new PayloadAttestationData
            {
                BeaconBlockRoot = Hash(0xE0),
                Slot = 11_649_024,
                PayloadPresent = true,
                BlobDataAvailable = false,
            },
            Signature = Signature(0xE1),
        };

        AssertRoundTripsAndMatchesRoot(message, "0xdddbeec88a8d1479ff53e12e675553f0bedbef6a503f8d43e1ce51f810e74c96");
    }

    /// <summary>
    /// The all-default (every field zero/empty) bid: the simplest instance whose root the progressive
    /// merkleization (active_fields-bitvector mix-in) can be checked against, the same way
    /// <c>Zeroed_checkpoint_hash_tree_root_matches_spec_value</c> checks a plain container.
    /// </summary>
    [Test]
    public void ExecutionPayloadBid_all_default_hash_tree_root_matches_an_independently_computed_value() =>
        AssertRoundTripsAndMatchesRoot(new ExecutionPayloadBid(), "0x83b932ee5875c06aa35328e3c3e3c976c703f2f4b1bc98e32991ceabbb2e4b63");

    [Test]
    public void ExecutionPayloadBid_with_values_round_trips_with_a_stable_root()
    {
        ExecutionPayloadBid bid = new()
        {
            ParentBlockHash = Hash(0x01),
            ParentBlockRoot = Hash(0x02),
            BlockHash = Hash(0x03),
            PrevRandao = Hash(0x04),
            FeeRecipient = new Address(Filled(Address.Size, 0x05)),
            GasLimit = 36_000_000,
            BuilderIndex = 3,
            Slot = 11_649_024,
            Value = 32_000_000_000,
            ExecutionPayment = 1_000_000_000,
            BlobKzgCommitments = [SszKzgCommitmentOf(0x06)],
            ExecutionRequestsRoot = Hash(0x07),
        };

        byte[] encoded = ExecutionPayloadBid.Encode(bid);
        ExecutionPayloadBid.Decode(encoded, out ExecutionPayloadBid decoded);
        Assert.That(ExecutionPayloadBid.Encode(decoded), Is.EqualTo(encoded));
        ExecutionPayloadBid.Merkleize(bid, out UInt256 root);
        ExecutionPayloadBid.Merkleize(decoded, out UInt256 decodedRoot);
        Assert.Multiple(() =>
        {
            Assert.That(decodedRoot, Is.EqualTo(root));
            Assert.That(root, Is.Not.EqualTo(UInt256.Zero));
            Assert.That(decoded.BlobKzgCommitments, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void SignedExecutionPayloadBid_round_trips_with_a_stable_root()
    {
        SignedExecutionPayloadBid signed = new()
        {
            Message = new ExecutionPayloadBid
            {
                ParentBlockHash = Hash(0x01),
                ParentBlockRoot = Hash(0x02),
                BlockHash = Hash(0x03),
                PrevRandao = Hash(0x04),
                FeeRecipient = new Address(Filled(Address.Size, 0x05)),
                GasLimit = 36_000_000,
                BuilderIndex = 3,
                Slot = 11_649_024,
                Value = 32_000_000_000,
                ExecutionPayment = 1_000_000_000,
                BlobKzgCommitments = [],
                ExecutionRequestsRoot = Hash(0x07),
            },
            Signature = Signature(0x08),
        };

        AssertRoundTrips(signed, SignedExecutionPayloadBid.Encode, SignedExecutionPayloadBid.Decode, SignedExecutionPayloadBid.Merkleize);
    }

    [Test]
    public void ExecutionPayloadEnvelope_round_trips_with_a_stable_root()
    {
        ExecutionPayloadEnvelope envelope = new()
        {
            Payload = RepresentativeExecutionPayload(),
            ExecutionRequests = new ExecutionRequestsGloas
            {
                Deposits = [],
                Withdrawals = [],
                Consolidations = [],
                BuilderDeposits =
                [
                    new BuilderDepositRequest { Pubkey = Pubkey(0x10), WithdrawalCredentials = Hash(0x11), Amount = 32_000_000_000, Signature = Signature(0x12) },
                ],
                BuilderExits = [],
            },
            BuilderIndex = 3,
            BeaconBlockRoot = Hash(0x20),
            ParentBeaconBlockRoot = Hash(0x21),
        };

        AssertRoundTrips(envelope, ExecutionPayloadEnvelope.Encode, ExecutionPayloadEnvelope.Decode, ExecutionPayloadEnvelope.Merkleize);
    }

    [Test]
    public void SignedExecutionPayloadEnvelope_round_trips_with_a_stable_root()
    {
        SignedExecutionPayloadEnvelope signed = new()
        {
            Message = new ExecutionPayloadEnvelope
            {
                Payload = RepresentativeExecutionPayload(),
                ExecutionRequests = new ExecutionRequestsGloas(),
                BuilderIndex = 3,
                BeaconBlockRoot = Hash(0x20),
                ParentBeaconBlockRoot = Hash(0x21),
            },
            Signature = Signature(0x22),
        };

        AssertRoundTrips(signed, SignedExecutionPayloadEnvelope.Encode, SignedExecutionPayloadEnvelope.Decode, SignedExecutionPayloadEnvelope.Merkleize);
    }

    [Test]
    public void PayloadAttestation_round_trips_with_a_stable_root()
    {
        BitArray bits = new(512);
        bits.Set(0, true);
        bits.Set(511, true);

        PayloadAttestation attestation = new()
        {
            AggregationBits = bits,
            Data = new PayloadAttestationData { BeaconBlockRoot = Hash(0x30), Slot = 100, PayloadPresent = true, BlobDataAvailable = true },
            Signature = Signature(0x31),
        };

        AssertRoundTrips(attestation, PayloadAttestation.Encode, PayloadAttestation.Decode, PayloadAttestation.Merkleize);
    }

    [Test]
    public void IndexedPayloadAttestation_round_trips_with_a_stable_root()
    {
        IndexedPayloadAttestation attestation = new()
        {
            AttestingIndices = [1, 2, 3],
            Data = new PayloadAttestationData { BeaconBlockRoot = Hash(0x30), Slot = 100, PayloadPresent = true, BlobDataAvailable = true },
            Signature = Signature(0x31),
        };

        AssertRoundTrips(attestation, IndexedPayloadAttestation.Encode, IndexedPayloadAttestation.Decode, IndexedPayloadAttestation.Merkleize);
    }

    [Test]
    public void BeaconBlockBodyGloas_round_trips_with_a_stable_root()
    {
        BeaconBlockBodyGloas body = new()
        {
            RandaoReveal = Signature(0x40),
            Eth1Data = new Eth1Data { DepositRoot = Hash(0x41), DepositCount = 1, BlockHash = Hash(0x42) },
            Graffiti = Hash(0x43),
            ProposerSlashings = [],
            AttesterSlashings = [],
            Attestations = [],
            Deposits = [],
            VoluntaryExits = [],
            SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512), SyncCommitteeSignature = Signature(0x44) },
            BlsToExecutionChanges = [],
            SignedExecutionPayloadBid = new SignedExecutionPayloadBid
            {
                Message = new ExecutionPayloadBid
                {
                    ParentBlockHash = Hash(0x45),
                    ParentBlockRoot = Hash(0x46),
                    BlockHash = Hash(0x47),
                    PrevRandao = Hash(0x48),
                    FeeRecipient = new Address(Filled(Address.Size, 0x49)),
                    GasLimit = 36_000_000,
                    BuilderIndex = ulong.MaxValue,
                    Slot = 123,
                    Value = 0,
                    ExecutionPayment = 0,
                    BlobKzgCommitments = [],
                    ExecutionRequestsRoot = Hash(0x4A),
                },
                Signature = Signature(0x4B),
            },
            PayloadAttestations = [],
            ParentExecutionRequests = new ExecutionRequestsGloas(),
        };

        AssertRoundTrips(body, BeaconBlockBodyGloas.Encode, BeaconBlockBodyGloas.Decode, BeaconBlockBodyGloas.Merkleize);
    }

    /// <summary>
    /// The all-default (every field zero/empty/512-of-zero) state: the same "simplest checkable
    /// instance" approach as the bid test above, sized up to a 46-field progressive container with
    /// several large fixed vectors (block_roots, randao_mixes, ptc_window, ...).
    /// </summary>
    [Test]
    public void BeaconStateGloas_all_default_hash_tree_root_matches_an_independently_computed_value()
    {
        BeaconStateGloas state = new()
        {
            Fork = new Fork(),
            LatestBlockHeader = new BeaconBlockHeader(),
            Eth1Data = new Eth1Data(),
            PreviousJustifiedCheckpoint = new Checkpoint(),
            CurrentJustifiedCheckpoint = new Checkpoint(),
            FinalizedCheckpoint = new Checkpoint(),
            CurrentSyncCommittee = new SyncCommittee(),
            NextSyncCommittee = new SyncCommittee(),
            LatestBlockHash = Hash256.Zero,
            ExecutionPayloadAvailability = new BitArray(8192),
            BuilderPendingPayments = [.. Enumerable.Repeat(0, 64).Select(static _ => new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() })],
            LatestExecutionPayloadBid = new ExecutionPayloadBid(),
            PtcWindow = [.. Enumerable.Repeat(0, 96).Select(static _ => new PayloadTimelinessCommittee())],
        };

        AssertRoundTripsAndMatchesRoot(state, "0x1971a1bc7e155511766c64b6a2121317d01fa040ffa6da5f93c3629f60fe3166");
    }

    private static ExecutionPayloadGloas RepresentativeExecutionPayload() => new()
    {
        ParentHash = Hash(0x50),
        FeeRecipient = new Address(Filled(Address.Size, 0x51)),
        StateRoot = Hash(0x52),
        ReceiptsRoot = Hash(0x53),
        LogsBloom = new Bloom(Filled(Bloom.ByteLength, 0x54)),
        PrevRandao = Hash(0x55),
        BlockNumber = 9_000_001,
        GasLimit = 36_000_000,
        GasUsed = 21_000,
        Timestamp = 1_791_294_816,
        ExtraData = Bytes.FromHexString("0xc0ffee"),
        BaseFeePerGas = 7,
        BlockHash = Hash(0x56),
        Transactions = [new TransactionGloas { Bytes = Bytes.FromHexString("0x02f87001020304") }],
        Withdrawals = [new Withdrawal { Index = 1, ValidatorIndex = 2, Address = new Address(Filled(Address.Size, 0x57)), Amount = 1_000_000 }],
        BlobGasUsed = 131_072,
        ExcessBlobGas = 0,
        BlockAccessList = Bytes.FromHexString("0xdeadbeef"),
        SlotNumber = 123,
    };

    private static void AssertRoundTripsAndMatchesRoot<T>(T value, string expectedRootHex) where T : class, ISszCodec<T>
    {
        AssertRoundTrips(value, T.Encode, T.Decode, T.Merkleize);
        Hash256 root = SszRoots.HashTreeRoot(value);
        Assert.That(root.ToString(), Is.EqualTo(expectedRootHex).IgnoreCase);
    }

    private static void AssertRoundTrips<T>(T value, System.Func<T, byte[]> encode, DecodeDelegate<T> decode, MerkleizeDelegate<T> merkleize)
    {
        byte[] encoded = encode(value);
        decode(encoded, out T decoded);
        byte[] reEncoded = encode(decoded);
        merkleize(value, out UInt256 originalRoot);
        merkleize(decoded, out UInt256 decodedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(reEncoded, Is.EqualTo(encoded));
            Assert.That(decodedRoot, Is.EqualTo(originalRoot));
        });
    }

    private delegate void DecodeDelegate<T>(ReadOnlySpan<byte> data, out T value);
    private delegate void MerkleizeDelegate<T>(T value, out UInt256 root);

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static Hash256 Hash(byte value) => new(Filled(Hash256.Size, value));

    private static BlsPublicKey Pubkey(byte value) => new(Filled(BlsPublicKey.Length, value));

    private static BlsSignature Signature(byte value) => new(Filled(BlsSignature.Length, value));

    private static Nethermind.Merge.Plugin.SszRest.SszKzgCommitment SszKzgCommitmentOf(byte value) =>
        Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(Filled(Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.KzgCommitmentLength, value));
}
