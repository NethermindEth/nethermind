//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;
using Shouldly;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszContainersTests
    {
        [SetUp]
        public void Setup()
        {
            Ssz.Init(
                32,
                4,
                2048,
                32,
                1024,
                8192,
                65536,
                8192,
                16_777_216,
                1_099_511_627_776,
                16,
                1,
                128,
                16,
                16
                );    
        }

        [Test]
        public void Fork_there_and_back()
        {
            Fork container = new Fork(new ForkVersion(new byte[] { 0x01, 0x00, 0x00, 0x00 }), new ForkVersion(new byte[] { 0x02, 0x00, 0x00, 0x00 }), new Epoch(3));
            Span<byte> encoded = new byte[Ssz.ForkLength];
            Ssz.Encode(encoded, container);
            Fork decoded = Ssz.DecodeFork(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Checkpoint_there_and_back()
        {
            Checkpoint container = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            Span<byte> encoded = new byte[Ssz.CheckpointLength];
            Ssz.Encode(encoded, container);
            Checkpoint decoded = Ssz.DecodeCheckpoint(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Validator_there_and_back()
        {
            Validator container = new Validator(
                SszTest.TestKey1, 
                Sha256.OfAnEmptyString, 
                Gwei.One, 
                true, 
                new Epoch(4), 
                new Epoch(5), 
                new Epoch(6), 
                new Epoch(7)
                );

            Span<byte> encoded = new byte[Ssz.ValidatorLength];
            Ssz.Encode(encoded, container);
            Validator decoded = Ssz.DecodeValidator(encoded);
            decoded.ShouldBe(container);
            //Assert.AreEqual(container, decoded);
            Assert.AreEqual(7, decoded.WithdrawableEpoch.Number);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attestation_data_there_and_back()
        {
            AttestationData container = new AttestationData(
                new Slot(1),
                new CommitteeIndex(2),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString));

            Span<byte> encoded = new byte[Ssz.AttestationDataLength];
            Ssz.Encode(encoded, container);
            AttestationData decoded = Ssz.DecodeAttestationData(encoded);
            Assert.AreEqual(container, decoded);

            Span<byte> encodedAgain = new byte[Ssz.AttestationDataLength];
            Ssz.Encode(encodedAgain, decoded);
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Indexed_attestation_there_and_back()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(2),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString));

            IndexedAttestation container = new IndexedAttestation(
                new ValidatorIndex[3],
                data, 
                SszTest.TestSig1);

            Span<byte> encoded = new byte[Ssz.IndexedAttestationLength(container)];
            Ssz.Encode(encoded, container);
            IndexedAttestation decoded = Ssz.DecodeIndexedAttestation(encoded);
            
            decoded.ShouldBe(container);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Pending_attestation_there_and_back()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(2),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString));

            PendingAttestation container = new PendingAttestation(
                new BitArray(new byte[3]),
                data,
                new Slot(7),
                new ValidatorIndex(13));

            Span<byte> encoded = new byte[Ssz.PendingAttestationLength(container)];
            Ssz.Encode(encoded, container);
            PendingAttestation? decoded = Ssz.DecodePendingAttestation(encoded);
            
            decoded.ShouldBe(container);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Eth1_data_there_and_back()
        {
            Eth1Data container = new Eth1Data(
                Sha256.OfAnEmptyString,
                1,
                Sha256.OfAnEmptyString);
            Span<byte> encoded = new byte[Ssz.Eth1DataLength];
            Ssz.Encode(encoded, container);
            Eth1Data decoded = Ssz.DecodeEth1Data(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Historical_batch_there_and_back()
        {
            Hash32[] blockRoots = Enumerable.Repeat(Hash32.Zero, Ssz.SlotsPerHistoricalRoot).ToArray();
            Hash32[] stateRoots = Enumerable.Repeat(Hash32.Zero, Ssz.SlotsPerHistoricalRoot).ToArray();
            blockRoots[3] = Sha256.OfAnEmptyString;
            stateRoots[7] = Sha256.OfAnEmptyString;
            HistoricalBatch container = new HistoricalBatch(blockRoots, stateRoots);
            Span<byte> encoded = new byte[Ssz.HistoricalBatchLength()];
            Ssz.Encode(encoded, container);
            HistoricalBatch? decoded = Ssz.DecodeHistoricalBatch(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Deposit_data_there_and_back()
        {
            DepositData container = new DepositData(
                SszTest.TestKey1,
                Sha256.OfAnEmptyString,
                Gwei.One,
                SszTest.TestSig1);
            Span<byte> encoded = new byte[Ssz.DepositDataLength];
            Ssz.Encode(encoded, container);
            DepositData decoded = Ssz.DecodeDepositData(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Beacon_block_header_there_and_back()
        {
            BeaconBlockHeader container = new BeaconBlockHeader(
                new Slot(1),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                SszTest.TestSig1);
            Span<byte> encoded = new byte[Ssz.BeaconBlockHeaderLength];
            Ssz.Encode(encoded, container);
            BeaconBlockHeader decoded = Ssz.DecodeBeaconBlockHeader(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Proposer_slashing_there_and_back()
        {
            BeaconBlockHeader header1 = new BeaconBlockHeader(
                new Slot(1),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                SszTest.TestSig1);

            BeaconBlockHeader header2 = new BeaconBlockHeader(
                new Slot(2),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                SszTest.TestSig1);

            ProposerSlashing container = new ProposerSlashing(
                new ValidatorIndex(1),
                header1,
                header2);

            Span<byte> encoded = new byte[Ssz.ProposerSlashingLength];
            Ssz.Encode(encoded, container);
            ProposerSlashing? decoded = Ssz.DecodeProposerSlashing(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attester_slashing_there_and_back()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(2),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString));

            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
                new ValidatorIndex[3],
                data,
                SszTest.TestSig1);

            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
                new ValidatorIndex[5],
                data,
                SszTest.TestSig1);

            AttesterSlashing container = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

            Span<byte> encoded = new byte[Ssz.AttesterSlashingLength(container)];
            Ssz.Encode(encoded, container);
            AttesterSlashing? decoded = Ssz.DecodeAttesterSlashing(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attestation_there_and_back()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(2),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString));

            Attestation container = new Attestation(
                new BitArray(new byte[] {1, 2, 3}),
                data,
                SszTest.TestSig1);

            Span<byte> encoded = new byte[Ssz.AttestationLength(container)];
            Ssz.Encode(encoded, container);
            Attestation? decoded = Ssz.DecodeAttestation(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Deposit_there_and_back()
        {
            DepositData data = new DepositData(
                SszTest.TestKey1,
                Sha256.OfAnEmptyString,
                Gwei.One,
                SszTest.TestSig1);

            Hash32[] proof = Enumerable.Repeat(Hash32.Zero, Ssz.DepositContractTreeDepth + 1).ToArray();
            proof[7] = Sha256.OfAnEmptyString;
            Deposit container = new Deposit(proof, data);

            Span<byte> encoded = new byte[Ssz.DepositLength()];
            Ssz.Encode(encoded, container);
            Deposit? decoded = Ssz.DecodeDeposit(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Voluntary_exit_there_and_back()
        {
            VoluntaryExit container = new VoluntaryExit(
                new Epoch(1),
                new ValidatorIndex(2), 
                SszTest.TestSig1);

            Span<byte> encoded = new byte[Ssz.VoluntaryExitLength];
            Ssz.Encode(encoded, container);
            VoluntaryExit? decoded = Ssz.DecodeVoluntaryExit(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Beacon_block_body_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                1,
                Sha256.OfAnEmptyString);

            BeaconBlockBody container = new BeaconBlockBody(
                SszTest.TestSig1,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[2],
                new AttesterSlashing[3], 
                new Attestation[4],
                new Deposit[5],
                new VoluntaryExit[6]
            );

            Span<byte> encoded = new byte[Ssz.BeaconBlockBodyLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlockBody decoded = Ssz.DecodeBeaconBlockBody(encoded);
            
            AssertBeaconBlockBodyEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }
        
        [Test]
        public void Beacon_block_body_more_detailed()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(4),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString));

            Attestation attestation = new Attestation(
                new BitArray(new byte[5]),
                data,
                SszTest.TestSig1);

            DepositData depositData = new DepositData(
                SszTest.TestKey1,
                Sha256.OfAnEmptyString,
                new Gwei(7),
                SszTest.TestSig1);

            Deposit deposit = new Deposit(new Hash32[Ssz.DepositContractTreeDepth + 1], depositData);

            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                SszTest.TestSig1);

            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                SszTest.TestSig1);

            AttesterSlashing slashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                9,
                Sha256.OfAnEmptyString);

            Attestation[] attestations = new Attestation[3];
            attestations[1] = attestation;

            Deposit[] deposits = new Deposit[3];
            deposits[2] = deposit;

            Bytes32 graffiti = new Bytes32(new byte[32]);
            
            AttesterSlashing[] attesterSlashings = new AttesterSlashing[3];
            attesterSlashings[0] = slashing;
            
            ProposerSlashing[] proposerSlashings = new ProposerSlashing[10];
            VoluntaryExit[] voluntaryExits = new VoluntaryExit[11];
            
            BeaconBlockBody body = new BeaconBlockBody(
                SszTest.TestSig1,
                eth1Data,
                graffiti,
                proposerSlashings,
                attesterSlashings,
                attestations,
                deposits,
                voluntaryExits
            );
            
            byte[] encoded = new byte[Ssz.BeaconBlockBodyLength(body)];
            Ssz.Encode(encoded, body);
        }

        [Test]
        public void Beacon_block_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                1,
                Sha256.OfAnEmptyString);

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                SszTest.TestSig1,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[2],
                new AttesterSlashing[3], 
                new Attestation[4],
                new Deposit[5],
                new VoluntaryExit[6]
            );

            BeaconBlock container = new BeaconBlock(
                new Slot(1),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                beaconBlockBody,
                SszTest.TestSig1);

            Span<byte> encoded = new byte[Ssz.BeaconBlockLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlock decoded = Ssz.DecodeBeaconBlock(encoded);

            AssertBeaconBlockEqual(container, decoded);

            Span<byte> encodedAgain = new byte[Ssz.BeaconBlockLength(container)];
            Ssz.Encode(encodedAgain, decoded);
            
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
            
            Merkle.Ize(out UInt256 root, container);
        }
        
        [Test]
        public void Beacon_state_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                1,
                Sha256.OfAnEmptyString);

            BeaconBlockHeader beaconBlockHeader = new BeaconBlockHeader(
                new Slot(14),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                SszTest.TestSig1);

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
                SszTest.TestSig1,
                eth1Data,
                new Bytes32(new byte[32]),
                new ProposerSlashing[2],
                new AttesterSlashing[3], 
                new Attestation[4],
                new Deposit[5],
                new VoluntaryExit[6]
            );

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot(1),
                Sha256.OfAnEmptyString,
                Sha256.OfAnEmptyString,
                beaconBlockBody,
                SszTest.TestSig1);

            BeaconState container = new BeaconState(
                123,
                new Slot(1),
                new Fork(new ForkVersion(new byte[] {0x05, 0x00, 0x00, 0x00}),
                    new ForkVersion(new byte[] {0x07, 0x00, 0x00, 0x00}), new Epoch(3)),
                beaconBlockHeader,
                new Hash32[Ssz.SlotsPerHistoricalRoot],
                new Hash32[Ssz.SlotsPerHistoricalRoot],
                new Hash32[13],
                eth1Data,
                new Eth1Data[2],
                1234,
                new Validator[7],
                new Gwei[3],
                new Hash32[Ssz.EpochsPerHistoricalVector],
                new Gwei[Ssz.EpochsPerSlashingsVector],
                new PendingAttestation[1],
                new PendingAttestation[11],
                new BitArray(new byte[] {0x09}),
                new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(5), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(7), Sha256.OfAnEmptyString)
            );
            
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            options.ConfigureNethermindCore2();
            TestContext.WriteLine("Original state: {0}", JsonSerializer.Serialize(container, options));

            int encodedLength = Ssz.BeaconStateLength(container);
            TestContext.WriteLine("Encoded length: {0}", encodedLength);
            Span<byte> encoded = new byte[encodedLength];
            Ssz.Encode(encoded, container);
            BeaconState decoded = Ssz.DecodeBeaconState(encoded);

            TestContext.WriteLine("Decoded state: {0}", JsonSerializer.Serialize(decoded, options));

            AssertBeaconStateEqual(container, decoded);

            Span<byte> encodedAgain = new byte[Ssz.BeaconStateLength(decoded)];
            Ssz.Encode(encodedAgain, decoded);

            byte[] encodedArray = encoded.ToArray();
            byte[] encodedAgainArray = encodedAgain.ToArray();
            
            encodedAgainArray.Length.ShouldBe(encodedArray.Length);
            //encodedAgainArray.ShouldBe(encodedArray);
            //Assert.True(Bytes.AreEqual(encodedAgain, encoded));
            
            Merkle.Ize(out UInt256 root, container);
        }

        private void AssertBeaconBlockBodyEqual(BeaconBlockBody expected, BeaconBlockBody actual)
        {
            actual.RandaoReveal.ShouldBe(expected.RandaoReveal);
            actual.Eth1Data.ShouldBe(expected.Eth1Data);
            actual.Graffiti.ShouldBe(expected.Graffiti);
            actual.ProposerSlashings.Count.ShouldBe(expected.ProposerSlashings.Count);
            actual.AttesterSlashings.Count.ShouldBe(expected.AttesterSlashings.Count);
            actual.Attestations.Count.ShouldBe(expected.Attestations.Count);
            actual.Deposits.Count.ShouldBe(expected.Deposits.Count);
            actual.VoluntaryExits.Count.ShouldBe(expected.VoluntaryExits.Count);

            actual.AttesterSlashings.ShouldBe(expected.AttesterSlashings);
            actual.ProposerSlashings.ShouldBe(expected.ProposerSlashings);
            actual.Attestations.ShouldBe(expected.Attestations);
            actual.Deposits.ShouldBe(expected.Deposits);
            actual.VoluntaryExits.ShouldBe(expected.VoluntaryExits);
        }

        private void AssertBeaconBlockEqual(BeaconBlock expected, BeaconBlock actual)
        {
            actual.Slot.ShouldBe(expected.Slot);
            actual.ParentRoot.ShouldBe(expected.ParentRoot);
            actual.StateRoot.ShouldBe(expected.StateRoot);
            actual.Signature.ShouldBe(expected.Signature);

            AssertBeaconBlockBodyEqual(expected.Body, actual.Body);
        }

        public void AssertBeaconStateEqual(BeaconState expected, BeaconState actual)
        {
            actual.GenesisTime.ShouldBe(expected.GenesisTime);
            actual.Slot.ShouldBe(expected.Slot);
            actual.Fork.ShouldBe(expected.Fork);
            actual.LatestBlockHeader.ShouldBe(expected.LatestBlockHeader);
            actual.BlockRoots.Count.ShouldBe(expected.BlockRoots?.Count ?? 0);
            actual.StateRoots.Count.ShouldBe(expected.StateRoots?.Count ?? 0);
            actual.HistoricalRoots.Count.ShouldBe(expected.HistoricalRoots?.Count ?? 0);
            actual.Eth1Data.ShouldBe(expected.Eth1Data);
            actual.Eth1DataVotes.Count.ShouldBe(expected.Eth1DataVotes?.Count ?? 0);
            actual.Eth1DepositIndex.ShouldBe(expected.Eth1DepositIndex);
            actual.Validators.Count.ShouldBe(expected.Validators?.Count ?? 0);
            actual.Balances.Count.ShouldBe(expected.Balances?.Count ?? 0);
            actual.RandaoMixes.Count.ShouldBe(expected.RandaoMixes?.Count ?? 0);
            actual.Slashings.Count.ShouldBe(expected.Slashings?.Count ?? 0);
            actual.PreviousEpochAttestations.Count.ShouldBe(expected.PreviousEpochAttestations?.Count ?? 0);
            actual.CurrentEpochAttestations.Count.ShouldBe(expected.CurrentEpochAttestations?.Count ?? 0);
            //expected.JustificationBits.Count.ShouldBe(actual.JustificationBits.Count);
            actual.PreviousJustifiedCheckpoint.ShouldBe(expected.PreviousJustifiedCheckpoint);
            actual.CurrentJustifiedCheckpoint.ShouldBe(expected.CurrentJustifiedCheckpoint);
            actual.FinalizedCheckpoint.ShouldBe(expected.FinalizedCheckpoint);
        }
    }
}