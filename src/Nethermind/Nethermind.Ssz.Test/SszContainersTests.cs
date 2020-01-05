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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
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
        [Test]
        public void Fork_there_and_back()
        {
            Fork container = new Fork(new ForkVersion(new byte[] { 0x01, 0x00, 0x00, 0x00 }), new ForkVersion(new byte[] { 0x02, 0x00, 0x00, 0x00 }), new Epoch(3));
            Span<byte> encoded = new byte[ByteLength.ForkLength];
            Ssz.Encode(encoded, container);
            Fork decoded = Ssz.DecodeFork(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Checkpoint_there_and_back()
        {
            Checkpoint container = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            Span<byte> encoded = new byte[ByteLength.CheckpointLength];
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

            Span<byte> encoded = new byte[ByteLength.ValidatorLength];
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

            Span<byte> encoded = new byte[ByteLength.AttestationDataLength];
            Ssz.Encode(encoded, container);
            AttestationData decoded = Ssz.DecodeAttestationData(encoded);
            Assert.AreEqual(container, decoded);

            Span<byte> encodedAgain = new byte[ByteLength.AttestationDataLength];
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

            Span<byte> encoded = new byte[ByteLength.IndexedAttestationLength(container)];
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

            Span<byte> encoded = new byte[ByteLength.PendingAttestationLength(container)];
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
            Span<byte> encoded = new byte[ByteLength.Eth1DataLength];
            Ssz.Encode(encoded, container);
            Eth1Data decoded = Ssz.DecodeEth1Data(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Historical_batch_there_and_back()
        {
            var blockRoots = Enumerable.Repeat(Hash32.Zero, Time.SlotsPerHistoricalRoot).ToArray();
            var stateRoots = Enumerable.Repeat(Hash32.Zero, Time.SlotsPerHistoricalRoot).ToArray();
            blockRoots[3] = Sha256.OfAnEmptyString;
            stateRoots[7] = Sha256.OfAnEmptyString;
            HistoricalBatch container = new HistoricalBatch(blockRoots, stateRoots);
            Span<byte> encoded = new byte[ByteLength.HistoricalBatchLength];
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
            Span<byte> encoded = new byte[ByteLength.DepositDataLength];
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
            Span<byte> encoded = new byte[ByteLength.BeaconBlockHeaderLength];
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

            Span<byte> encoded = new byte[ByteLength.ProposerSlashingLength];
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

            Span<byte> encoded = new byte[ByteLength.AttesterSlashingLength(container)];
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

            Span<byte> encoded = new byte[ByteLength.AttestationLength(container)];
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

            Hash32[] proof = Enumerable.Repeat(Hash32.Zero, ByteLength.ContractTreeDepth + 1).ToArray();
            proof[7] = Sha256.OfAnEmptyString;
            Deposit container = new Deposit(proof, data);

            Span<byte> encoded = new byte[ByteLength.DepositLength];
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

            Span<byte> encoded = new byte[ByteLength.VoluntaryExitLength];
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

            BeaconBlockBody container = new BeaconBlockBody();
            container.RandaoReversal = SszTest.TestSig1;
            container.Eth1Data = eth1Data;
            container.Graffiti = new byte[32];
            container.ProposerSlashings = new ProposerSlashing[2];
            container.AttesterSlashings = new AttesterSlashing[3];
            container.Attestations = new Attestation[4];
            container.Deposits = new Deposit[5];
            container.VoluntaryExits = new VoluntaryExit[6];

            Span<byte> encoded = new byte[BeaconBlockBody.SszLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlockBody decoded = Ssz.DecodeBeaconBlockBody(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Beacon_block_body_more_detailed()
        {
            BeaconBlockBody body = new BeaconBlockBody();

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

            Deposit deposit = new Deposit(new Hash32[ByteLength.ContractTreeDepth + 1], depositData);

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

            body.Attestations = new Attestation[3];
            body.Attestations[1] = attestation;

            body.Deposits = new Deposit[3];
            body.Deposits[2] = deposit;

            body.Graffiti = new byte[32];
            body.AttesterSlashings = new AttesterSlashing[3];
            body.AttesterSlashings[0] = slashing;
            body.Eth1Data = eth1Data;
            body.ProposerSlashings = new ProposerSlashing[10];
            body.RandaoReversal = SszTest.TestSig1;
            body.VoluntaryExits = new VoluntaryExit[11];

            byte[] encoded = new byte[BeaconBlockBody.SszLength(body)];
            Ssz.Encode(encoded, body);
        }

        [Test]
        public void Beacon_block_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                1,
                Sha256.OfAnEmptyString);

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody();
            beaconBlockBody.RandaoReversal = SszTest.TestSig1;
            beaconBlockBody.Eth1Data = eth1Data;
            beaconBlockBody.Graffiti = new byte[32];
            beaconBlockBody.ProposerSlashings = new ProposerSlashing[2];
            beaconBlockBody.AttesterSlashings = new AttesterSlashing[3];
            beaconBlockBody.Attestations = new Attestation[4];
            beaconBlockBody.Deposits = new Deposit[5];
            beaconBlockBody.VoluntaryExits = new VoluntaryExit[6];

            BeaconBlock container = new BeaconBlock();
            container.Body = beaconBlockBody;
            container.Signature = SszTest.TestSig1;
            container.Slot = new Slot(1);
            container.ParentRoot = Sha256.OfAnEmptyString;
            container.StateRoot = Sha256.OfAnEmptyString;

            Span<byte> encoded = new byte[BeaconBlock.SszLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlock decoded = Ssz.DecodeBeaconBlock(encoded);
            Assert.AreEqual(container, decoded);

            Span<byte> encodedAgain = new byte[BeaconBlock.SszLength(container)];
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

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody();
            beaconBlockBody.RandaoReversal = SszTest.TestSig1;
            beaconBlockBody.Eth1Data = eth1Data;
            beaconBlockBody.Graffiti = new byte[32];
            beaconBlockBody.ProposerSlashings = new ProposerSlashing[2];
            beaconBlockBody.AttesterSlashings = new AttesterSlashing[3];
            beaconBlockBody.Attestations = new Attestation[4];
            beaconBlockBody.Deposits = new Deposit[5];
            beaconBlockBody.VoluntaryExits = new VoluntaryExit[6];

            BeaconBlock beaconBlock = new BeaconBlock();
            beaconBlock.Body = beaconBlockBody;
            beaconBlock.Signature = SszTest.TestSig1;
            beaconBlock.Slot = new Slot(1);
            beaconBlock.ParentRoot = Sha256.OfAnEmptyString;
            beaconBlock.StateRoot = Sha256.OfAnEmptyString;

            BeaconState container = new BeaconState();
            container.Balances = new Gwei[3];
            container.Fork = new Fork(new ForkVersion( new byte[] { 0x05, 0x00, 0x00, 0x00 }), new ForkVersion(new byte[] { 0x07, 0x00, 0x00, 0x00 }), new Epoch(3));
            container.Slashings = new Gwei[Time.EpochsPerSlashingsVector];
            container.Slot = new Slot(1);
            container.Validators = new Validator[7];
            container.BlockRoots = new Hash32[Time.SlotsPerHistoricalRoot];
            container.StateRoots = new Hash32[Time.SlotsPerHistoricalRoot];
            container.Eth1Data = eth1Data;
            container.Eth1DataVotes = new Eth1Data[2];
            container.PreviousJustifiedCheckpoint = new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString);
            container.CurrentJustifiedCheckpoint = new Checkpoint(new Epoch(5), Sha256.OfAnEmptyString);
            container.FinalizedCheckpoint = new Checkpoint(new Epoch(7), Sha256.OfAnEmptyString);
            container.GenesisTime = 123;
            container.HistoricalRoots = new Hash32[13];
            container.JustificationBits = 9;
            container.RandaoMixes = new Hash32[Time.EpochsPerHistoricalVector];
            container.PreviousEpochAttestations = new PendingAttestation[1];
            container.CurrentEpochAttestations = new PendingAttestation[11];
            container.Eth1DepositIndex = 1234;
            container.LatestBlockHeader = beaconBlockHeader;

            Span<byte> encoded = new byte[BeaconState.SszLength(container)];
            Ssz.Encode(encoded, container);
            BeaconState decoded = Ssz.DecodeBeaconState(encoded);
            Assert.AreEqual(container, decoded);

            Span<byte> encodedAgain = new byte[BeaconState.SszLength(decoded)];
            Ssz.Encode(encodedAgain, decoded);
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
            
            Merkle.Ize(out UInt256 root, container);
        }
    }
}