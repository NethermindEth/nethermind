//// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
//// SPDX-License-Identifier: LGPL-3.0-only

//using System;
//using System.Collections;
//using System.Linq;
//using System.Text.Json;
//using Nethermind.Core.Crypto;
//using Nethermind.Core.Extensions;
//using Nethermind.Core2;
//using Nethermind.Core2.Containers;
//using Nethermind.Core2.Crypto;
//using Nethermind.Core2.Json;
//using Nethermind.Core2.Types;
//using Nethermind.Dirichlet.Numerics;
//using Nethermind.Merkleization;
//using NUnit.Framework;
//using Bytes = Nethermind.Core.Extensions.Bytes;
//using Shouldly;

//namespace Nethermind.Serialization.Ssz.Test
//{
//    [TestFixture]
//    public class SszContainersTests
//    {
//        public static BlsPublicKey TestKey1 = new BlsPublicKey(
//            "0x000102030405060708090a0b0c0d0e0f" +
//            "101112131415161718191a1b1c1d1e1f" +
//            "202122232425262728292a2b2c2d2e2f");

//        public static BlsSignature TestSig1 = new BlsSignature(new byte[BlsSignature.Length]);

//        [SetUp]
//        public void Setup()
//        {
//            Ssz.Init(
//                32,
//                4,
//                2048,
//                32,
//                1024,
//                8192,
//                65536,
//                8192,
//                16_777_216,
//                1_099_511_627_776,
//                16,
//                1,
//                128,
//                16,
//                16
//                );
//        }

//        //[Test]
//        public void Fork_there_and_back()
//        {
//            Fork container = new Fork(new ForkVersion(new byte[] { 0x01, 0x00, 0x00, 0x00 }), new ForkVersion(new byte[] { 0x02, 0x00, 0x00, 0x00 }), new Epoch(3));
//            Span<byte> encoded = new byte[Ssz.ForkLength];
//            Ssz.Encode(encoded, container);
//            Fork decoded = Ssz.DecodeFork(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Checkpoint_there_and_back()
//        {
//            Checkpoint container = new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString);
//            Span<byte> encoded = new byte[Ssz.CheckpointLength];
//            Ssz.Encode(encoded, container);
//            Checkpoint decoded = Ssz.DecodeCheckpoint(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Validator_there_and_back()
//        {
//            Validator container = new Validator(
//                TestKey1,
//                Sha256.Bytes32OfAnEmptyString,
//                Gwei.One,
//                true,
//                new Epoch(4),
//                new Epoch(5),
//                new Epoch(6),
//                new Epoch(7)
//                );

//            Span<byte> encoded = new byte[Ssz.ValidatorLength];
//            Ssz.Encode(encoded, container);
//            Validator decoded = Ssz.DecodeValidator(encoded);
//            decoded.ShouldBe(container);
//            //Assert.AreEqual(container, decoded);
//            Assert.AreEqual(7, decoded.WithdrawableEpoch.Number);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Attestation_data_there_and_back()
//        {
//            AttestationData container = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(2),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString));

//            Span<byte> encoded = new byte[Ssz.AttestationDataLength];
//            Ssz.Encode(encoded, container);
//            AttestationData decoded = Ssz.DecodeAttestationData(encoded);
//            Assert.AreEqual(container, decoded);

//            Span<byte> encodedAgain = new byte[Ssz.AttestationDataLength];
//            Ssz.Encode(encodedAgain, decoded);
//            Assert.True(Bytes.AreEqual(encodedAgain, encoded));

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Indexed_attestation_there_and_back()
//        {
//            AttestationData data = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(2),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString));

//            IndexedAttestation container = new IndexedAttestation(
//                new ValidatorIndex[3],
//                data,
//                TestSig1);

//            Span<byte> encoded = new byte[Ssz.IndexedAttestationLength(container)];
//            Ssz.Encode(encoded, container);
//            IndexedAttestation decoded = Ssz.DecodeIndexedAttestation(encoded);

//            decoded.ShouldBe(container);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Pending_attestation_there_and_back()
//        {
//            AttestationData data = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(2),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString));

//            PendingAttestation container = new PendingAttestation(
//                new BitArray(new byte[3]),
//                data,
//                new Slot(7),
//                new ValidatorIndex(13));

//            Span<byte> encoded = new byte[Ssz.PendingAttestationLength(container)];
//            Ssz.Encode(encoded, container);
//            PendingAttestation? decoded = Ssz.DecodePendingAttestation(encoded);

//            decoded.ShouldBe(container);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Eth1_data_there_and_back()
//        {
//            Eth1Data container = new Eth1Data(
//                Sha256.RootOfAnEmptyString,
//                1,
//                Sha256.Bytes32OfAnEmptyString);
//            Span<byte> encoded = new byte[Ssz.Eth1DataLength];
//            Ssz.Encode(encoded, container);
//            Eth1Data decoded = Ssz.DecodeEth1Data(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Historical_batch_there_and_back()
//        {
//            Root[] blockRoots = Enumerable.Repeat(Root.Zero, Ssz.SlotsPerHistoricalRoot).ToArray();
//            Root[] stateRoots = Enumerable.Repeat(Root.Zero, Ssz.SlotsPerHistoricalRoot).ToArray();
//            blockRoots[3] = Sha256.RootOfAnEmptyString;
//            stateRoots[7] = Sha256.RootOfAnEmptyString;
//            HistoricalBatch container = new HistoricalBatch(blockRoots, stateRoots);
//            Span<byte> encoded = new byte[Ssz.HistoricalBatchLength()];
//            Ssz.Encode(encoded, container);
//            HistoricalBatch? decoded = Ssz.DecodeHistoricalBatch(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Deposit_data_there_and_back()
//        {
//            DepositData container = new DepositData(
//                TestKey1,
//                Sha256.Bytes32OfAnEmptyString,
//                Gwei.One,
//                TestSig1);
//            Span<byte> encoded = new byte[Ssz.DepositDataLength];
//            Ssz.Encode(encoded, container);
//            DepositData decoded = Ssz.DecodeDepositData(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Beacon_block_header_there_and_back()
//        {
//            BeaconBlockHeader container = new BeaconBlockHeader(
//                new Slot(1),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString);
//            Span<byte> encoded = new byte[Ssz.BeaconBlockHeaderLength];
//            Ssz.Encode(encoded, container);
//            BeaconBlockHeader decoded = Ssz.DecodeBeaconBlockHeader(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Proposer_slashing_there_and_back()
//        {
//            BeaconBlockHeader header1 = new BeaconBlockHeader(
//                new Slot(1),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString);

//            BeaconBlockHeader header2 = new BeaconBlockHeader(
//                new Slot(2),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString);

//            ProposerSlashing container = new ProposerSlashing(
//                new ValidatorIndex(1),
//                new SignedBeaconBlockHeader(header1, TestSig1),
//                new SignedBeaconBlockHeader(header2, TestSig1));

//            Span<byte> encoded = new byte[Ssz.ProposerSlashingLength];
//            Ssz.Encode(encoded, container);
//            ProposerSlashing? decoded = Ssz.DecodeProposerSlashing(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Attester_slashing_there_and_back()
//        {
//            AttestationData data = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(2),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString));

//            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
//                new ValidatorIndex[3],
//                data,
//                TestSig1);

//            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
//                new ValidatorIndex[5],
//                data,
//                TestSig1);

//            AttesterSlashing container = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

//            Span<byte> encoded = new byte[Ssz.AttesterSlashingLength(container)];
//            Ssz.Encode(encoded, container);
//            AttesterSlashing? decoded = Ssz.DecodeAttesterSlashing(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Attestation_there_and_back()
//        {
//            AttestationData data = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(2),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(1), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString));

//            Attestation container = new Attestation(
//                new BitArray(new byte[] { 1, 2, 3 }),
//                data,
//                TestSig1);

//            Span<byte> encoded = new byte[Ssz.AttestationLength(container)];
//            Ssz.Encode(encoded, container);
//            Attestation decoded = Ssz.DecodeAttestation(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Deposit_there_and_back()
//        {
//            DepositData data = new DepositData(
//                TestKey1,
//                Sha256.Bytes32OfAnEmptyString,
//                Gwei.One,
//                TestSig1);

//            Bytes32[] proof = Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1).ToArray();
//            proof[7] = Sha256.Bytes32OfAnEmptyString;
//            Deposit container = new Deposit(proof, new Ref<DepositData>(data));

//            Span<byte> encoded = new byte[Ssz.DepositLength()];
//            Ssz.Encode(encoded, container);
//            Deposit? decoded = Ssz.DecodeDeposit(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Voluntary_exit_there_and_back()
//        {
//            VoluntaryExit container = new VoluntaryExit(
//                new Epoch(1),
//                new ValidatorIndex(2));

//            Span<byte> encoded = new byte[Ssz.VoluntaryExitLength];
//            Ssz.Encode(encoded, container);
//            VoluntaryExit? decoded = Ssz.DecodeVoluntaryExit(encoded);
//            Assert.AreEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Beacon_block_body_there_and_back()
//        {
//            Eth1Data eth1Data = new Eth1Data(
//                Sha256.RootOfAnEmptyString,
//                1,
//                Sha256.Bytes32OfAnEmptyString);

//            Deposit zeroDeposit = new Deposit(Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1), new Ref<DepositData>(DepositData.Zero));
//            BeaconBlockBody container = new BeaconBlockBody(
//                TestSig1,
//                eth1Data,
//                new Bytes32(new byte[32]),
//                Enumerable.Repeat(ProposerSlashing.Zero, 2).ToArray(),
//                Enumerable.Repeat(AttesterSlashing.Zero, 3).ToArray(),
//                Enumerable.Repeat(Attestation.Zero, 4).ToArray(),
//                Enumerable.Repeat(zeroDeposit, 5).ToArray(),
//                Enumerable.Repeat(SignedVoluntaryExit.Zero, 6).ToArray()
//            );

//            Span<byte> encoded = new byte[Ssz.BeaconBlockBodyLength(container)];
//            Ssz.Encode(encoded, container);
//            BeaconBlockBody decoded = Ssz.DecodeBeaconBlockBody(encoded);

//            AssertBeaconBlockBodyEqual(container, decoded);

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Beacon_block_body_more_detailed()
//        {
//            AttestationData data = new AttestationData(
//                new Slot(1),
//                new CommitteeIndex(4),
//                Sha256.RootOfAnEmptyString,
//                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(3), Sha256.RootOfAnEmptyString));

//            Attestation attestation = new Attestation(
//                new BitArray(new byte[5]),
//                data,
//                TestSig1);

//            Ref<DepositData> depositData = new Ref<DepositData>(new DepositData(
//                TestKey1,
//                Sha256.Bytes32OfAnEmptyString,
//                new Gwei(7),
//                TestSig1));

//            Deposit deposit = new Deposit(Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1), depositData);

//            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
//                new ValidatorIndex[8],
//                data,
//                TestSig1);

//            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
//                new ValidatorIndex[8],
//                data,
//                TestSig1);

//            AttesterSlashing slashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

//            Eth1Data eth1Data = new Eth1Data(
//                Sha256.RootOfAnEmptyString,
//                9,
//                Sha256.Bytes32OfAnEmptyString);

//            Attestation[] attestations = Enumerable.Repeat(Attestation.Zero, 3).ToArray();
//            attestations[1] = attestation;

//            Deposit zeroDeposit = new Deposit(Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1), new Ref<DepositData>(DepositData.Zero));
//            Deposit[] deposits = Enumerable.Repeat(zeroDeposit, 3).ToArray();
//            deposits[2] = deposit;

//            Bytes32 graffiti = new Bytes32(new byte[32]);

//            AttesterSlashing[] attesterSlashings = Enumerable.Repeat(AttesterSlashing.Zero, 3).ToArray();
//            attesterSlashings[0] = slashing;

//            ProposerSlashing[] proposerSlashings = Enumerable.Repeat(ProposerSlashing.Zero, 10).ToArray();

//            SignedVoluntaryExit[] signedVoluntaryExits = Enumerable.Repeat(SignedVoluntaryExit.Zero, 11).ToArray();

//            BeaconBlockBody body = new BeaconBlockBody(
//                TestSig1,
//                eth1Data,
//                graffiti,
//                proposerSlashings,
//                attesterSlashings,
//                attestations,
//                deposits,
//                signedVoluntaryExits
//            );

//            byte[] encoded = new byte[Ssz.BeaconBlockBodyLength(body)];
//            Ssz.Encode(encoded, body);
//        }

//        [Test]
//        public void Beacon_block_there_and_back()
//        {
//            Eth1Data eth1Data = new Eth1Data(
//                Sha256.RootOfAnEmptyString,
//                1,
//                Sha256.Bytes32OfAnEmptyString);

//            Deposit zeroDeposit = new Deposit(Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1), new Ref<DepositData>(DepositData.Zero));
//            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
//                TestSig1,
//                eth1Data,
//                new Bytes32(new byte[32]),
//                Enumerable.Repeat(ProposerSlashing.Zero, 2).ToArray(),
//                Enumerable.Repeat(AttesterSlashing.Zero, 3).ToArray(),
//                Enumerable.Repeat(Attestation.Zero, 4).ToArray(),
//                Enumerable.Repeat(zeroDeposit, 5).ToArray(),
//                Enumerable.Repeat(SignedVoluntaryExit.Zero, 6).ToArray()
//            );

//            BeaconBlock container = new BeaconBlock(
//                new Slot(1),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                beaconBlockBody);

//            Span<byte> encoded = new byte[Ssz.BeaconBlockLength(container)];
//            Ssz.Encode(encoded, container);
//            BeaconBlock decoded = Ssz.DecodeBeaconBlock(encoded);

//            AssertBeaconBlockEqual(container, decoded);

//            Span<byte> encodedAgain = new byte[Ssz.BeaconBlockLength(container)];
//            Ssz.Encode(encodedAgain, decoded);

//            Assert.True(Bytes.AreEqual(encodedAgain, encoded));

//            Merkle.Ize(out UInt256 root, container);
//        }

//        [Test]
//        public void Beacon_state_there_and_back()
//        {
//            Eth1Data eth1Data = new Eth1Data(
//                Sha256.RootOfAnEmptyString,
//                1,
//                Sha256.Bytes32OfAnEmptyString);

//            BeaconBlockHeader beaconBlockHeader = new BeaconBlockHeader(
//                new Slot(14),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString);

//            Deposit zeroDeposit = new Deposit(Enumerable.Repeat(Bytes32.Zero, Ssz.DepositContractTreeDepth + 1), new Ref<DepositData>(DepositData.Zero));
//            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
//                TestSig1,
//                eth1Data,
//                new Bytes32(new byte[32]),
//                Enumerable.Repeat(ProposerSlashing.Zero, 2).ToArray(),
//                Enumerable.Repeat(AttesterSlashing.Zero, 3).ToArray(),
//                Enumerable.Repeat(Attestation.Zero, 4).ToArray(),
//                Enumerable.Repeat(zeroDeposit, 5).ToArray(),
//                Enumerable.Repeat(SignedVoluntaryExit.Zero, 6).ToArray()
//            );

//            BeaconBlock beaconBlock = new BeaconBlock(
//                new Slot(1),
//                Sha256.RootOfAnEmptyString,
//                Sha256.RootOfAnEmptyString,
//                beaconBlockBody);

//            BeaconState container = new BeaconState(
//                123,
//                new Slot(1),
//                new Fork(new ForkVersion(new byte[] { 0x05, 0x00, 0x00, 0x00 }),
//                    new ForkVersion(new byte[] { 0x07, 0x00, 0x00, 0x00 }), new Epoch(3)),
//                beaconBlockHeader,
//                Enumerable.Repeat(Root.Zero, Ssz.SlotsPerHistoricalRoot).ToArray(),
//                Enumerable.Repeat(Root.Zero, Ssz.SlotsPerHistoricalRoot).ToArray(),
//                Enumerable.Repeat(Root.Zero, 13).ToArray(),
//                eth1Data,
//                Enumerable.Repeat(Eth1Data.Zero, 2).ToArray(),
//                1234,
//                Enumerable.Repeat(Validator.Zero, 7).ToArray(),
//                new Gwei[3],
//                Enumerable.Repeat(Bytes32.Zero, Ssz.EpochsPerHistoricalVector).ToArray(),
//                new Gwei[Ssz.EpochsPerSlashingsVector],
//                Enumerable.Repeat(PendingAttestation.Zero, 1).ToArray(),
//                Enumerable.Repeat(PendingAttestation.Zero, 11).ToArray(),
//                new BitArray(new byte[] { 0x09 }),
//                new Checkpoint(new Epoch(3), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(5), Sha256.RootOfAnEmptyString),
//                new Checkpoint(new Epoch(7), Sha256.RootOfAnEmptyString)
//            );

//            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
//            options.ConfigureNethermindCore2();
//            TestContext.WriteLine("Original state: {0}", JsonSerializer.Serialize(container, options));

//            int encodedLength = Ssz.BeaconStateLength(container);
//            TestContext.WriteLine("Encoded length: {0}", encodedLength);
//            Span<byte> encoded = new byte[encodedLength];
//            Ssz.Encode(encoded, container);
//            BeaconState decoded = Ssz.DecodeBeaconState(encoded);

//            TestContext.WriteLine("Decoded state: {0}", JsonSerializer.Serialize(decoded, options));

//            AssertBeaconStateEqual(container, decoded);

//            Span<byte> encodedAgain = new byte[Ssz.BeaconStateLength(decoded)];
//            Ssz.Encode(encodedAgain, decoded);

//            byte[] encodedArray = encoded.ToArray();
//            byte[] encodedAgainArray = encodedAgain.ToArray();

//            encodedAgainArray.Length.ShouldBe(encodedArray.Length);
//            //encodedAgainArray.ShouldBe(encodedArray);
//            //Assert.True(Bytes.AreEqual(encodedAgain, encoded));

//            Merkle.Ize(out UInt256 root, container);
//        }

//        private void AssertBeaconBlockBodyEqual(BeaconBlockBody expected, BeaconBlockBody actual)
//        {
//            actual.RandaoReveal.ShouldBe(expected.RandaoReveal);
//            actual.Eth1Data.ShouldBe(expected.Eth1Data);
//            actual.Graffiti.ShouldBe(expected.Graffiti);
//            actual.ProposerSlashings.Count.ShouldBe(expected.ProposerSlashings.Count);
//            actual.AttesterSlashings.Count.ShouldBe(expected.AttesterSlashings.Count);
//            actual.Attestations.Count.ShouldBe(expected.Attestations.Count);
//            actual.Deposits.Count.ShouldBe(expected.Deposits.Count);
//            actual.VoluntaryExits.Count.ShouldBe(expected.VoluntaryExits.Count);

//            actual.AttesterSlashings.ShouldBe(expected.AttesterSlashings);
//            actual.ProposerSlashings.ShouldBe(expected.ProposerSlashings);
//            actual.Attestations.ShouldBe(expected.Attestations);
//            actual.Deposits.ShouldBe(expected.Deposits);
//            actual.VoluntaryExits.ShouldBe(expected.VoluntaryExits);
//        }

//        private void AssertBeaconBlockEqual(BeaconBlock expected, BeaconBlock actual)
//        {
//            actual.Slot.ShouldBe(expected.Slot);
//            actual.ParentRoot.ShouldBe(expected.ParentRoot);
//            actual.StateRoot.ShouldBe(expected.StateRoot);

//            AssertBeaconBlockBodyEqual(expected.Body, actual.Body);
//        }

//        public void AssertBeaconStateEqual(BeaconState expected, BeaconState actual)
//        {
//            actual.GenesisTime.ShouldBe(expected.GenesisTime);
//            actual.Slot.ShouldBe(expected.Slot);
//            actual.Fork.ShouldBe(expected.Fork);
//            actual.LatestBlockHeader.ShouldBe(expected.LatestBlockHeader);
//            actual.BlockRoots.Count.ShouldBe(expected.BlockRoots?.Count ?? 0);
//            actual.StateRoots.Count.ShouldBe(expected.StateRoots?.Count ?? 0);
//            actual.HistoricalRoots.Count.ShouldBe(expected.HistoricalRoots?.Count ?? 0);
//            actual.Eth1Data.ShouldBe(expected.Eth1Data);
//            actual.Eth1DataVotes.Count.ShouldBe(expected.Eth1DataVotes?.Count ?? 0);
//            actual.Eth1DepositIndex.ShouldBe(expected.Eth1DepositIndex);
//            actual.Validators.Count.ShouldBe(expected.Validators?.Count ?? 0);
//            actual.Balances.Count.ShouldBe(expected.Balances?.Count ?? 0);
//            actual.RandaoMixes.Count.ShouldBe(expected.RandaoMixes?.Count ?? 0);
//            actual.Slashings.Count.ShouldBe(expected.Slashings?.Count ?? 0);
//            actual.PreviousEpochAttestations.Count.ShouldBe(expected.PreviousEpochAttestations?.Count ?? 0);
//            actual.CurrentEpochAttestations.Count.ShouldBe(expected.CurrentEpochAttestations?.Count ?? 0);
//            //expected.JustificationBits.Count.ShouldBe(actual.JustificationBits.Count);
//            actual.PreviousJustifiedCheckpoint.ShouldBe(expected.PreviousJustifiedCheckpoint);
//            actual.CurrentJustifiedCheckpoint.ShouldBe(expected.CurrentJustifiedCheckpoint);
//            actual.FinalizedCheckpoint.ShouldBe(expected.FinalizedCheckpoint);
//        }
//    }
//}
