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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszContainersTests
    {
        [Test]
        public void Fork_there_and_back()
        {
            Fork container = new Fork(new ForkVersion(1), new ForkVersion(2), new Epoch(3));
            Span<byte> encoded = new byte[Fork.SszLength];
            Ssz.Encode(encoded, container);
            Fork decoded = Ssz.DecodeFork(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Checkpoint_there_and_back()
        {
            Checkpoint container = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            Span<byte> encoded = new byte[Checkpoint.SszLength];
            Ssz.Encode(encoded, container);
            Checkpoint decoded = Ssz.DecodeCheckpoint(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Validator_there_and_back()
        {
            Validator container = new Validator(BlsPublicKey.TestKey1);
            container.Slashed = true;
            container.WithdrawalCredentials = Sha256.OfAnEmptyString;
            container.EffectiveBalance = Gwei.One;
            container.ActivationEligibilityEpoch = new Epoch(4);
            container.ActivationEpoch = new Epoch(5);
            container.ExitEpoch = new Epoch(6);
            container.ActivationEligibilityEpoch = new Epoch(7);

            Span<byte> encoded = new byte[Validator.SszLength];
            Ssz.Encode(encoded, container);
            Validator decoded = Ssz.DecodeValidator(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attestation_data_there_and_back()
        {
            AttestationData container = new AttestationData();
            container.Slot = new Slot(1);
            container.CommitteeIndex = new CommitteeIndex(2);
            container.BeaconBlockRoot = Sha256.OfAnEmptyString;
            container.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            container.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            Span<byte> encoded = new byte[AttestationData.SszLength];
            Ssz.Encode(encoded, container);
            AttestationData decoded = Ssz.DecodeAttestationData(encoded);
            Assert.AreEqual(container, decoded);

            Span<byte> encodedAgain = new byte[AttestationData.SszLength];
            Ssz.Encode(encodedAgain, decoded);
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Indexed_attestation_there_and_back()
        {
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.CommitteeIndex = new CommitteeIndex(2);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            data.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            IndexedAttestation container = new IndexedAttestation();
            container.AttestingIndices = new ValidatorIndex[3];
            container.Data = data;
            container.Signature = BlsSignature.TestSig1;

            Span<byte> encoded = new byte[IndexedAttestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            IndexedAttestation decoded = Ssz.DecodeIndexedAttestation(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Pending_attestation_there_and_back()
        {
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.CommitteeIndex = new CommitteeIndex(2);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            data.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            PendingAttestation container = new PendingAttestation();
            container.AggregationBits = new byte[3];
            container.Data = data;
            container.InclusionDelay = new Slot(7);
            container.ProposerIndex = new ValidatorIndex(13);

            Span<byte> encoded = new byte[PendingAttestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            PendingAttestation? decoded = Ssz.DecodePendingAttestation(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Eth1_data_there_and_back()
        {
            Eth1Data container = new Eth1Data();
            container.BlockHash = Sha256.OfAnEmptyString;
            container.DepositCount = 1;
            container.DepositRoot = Sha256.OfAnEmptyString;
            Span<byte> encoded = new byte[Eth1Data.SszLength];
            Ssz.Encode(encoded, container);
            Eth1Data decoded = Ssz.DecodeEth1Data(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Historical_batch_there_and_back()
        {
            HistoricalBatch container = new HistoricalBatch();
            container.BlockRoots[3] = Sha256.OfAnEmptyString;
            container.StateRoots[7] = Sha256.OfAnEmptyString;
            Span<byte> encoded = new byte[HistoricalBatch.SszLength];
            Ssz.Encode(encoded, container);
            HistoricalBatch? decoded = Ssz.DecodeHistoricalBatch(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Deposit_data_there_and_back()
        {
            DepositData container = new DepositData();
            container.PublicKey = BlsPublicKey.TestKey1;
            container.WithdrawalCredentials = Sha256.OfAnEmptyString;
            container.Amount = Gwei.One;
            container.Signature = BlsSignature.TestSig1;
            Span<byte> encoded = new byte[DepositData.SszLength];
            Ssz.Encode(encoded, container);
            DepositData decoded = Ssz.DecodeDepositData(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Beacon_block_header_there_and_back()
        {
            BeaconBlockHeader container = new BeaconBlockHeader();
            container.Slot = new Slot(1);
            container.ParentRoot = Sha256.OfAnEmptyString;
            container.BodyRoot = Sha256.OfAnEmptyString;
            container.StateRoot = Sha256.OfAnEmptyString;
            container.Signature = BlsSignature.TestSig1;
            Span<byte> encoded = new byte[BeaconBlockHeader.SszLength];
            Ssz.Encode(encoded, container);
            BeaconBlockHeader decoded = Ssz.DecodeBeaconBlockHeader(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Proposer_slashing_there_and_back()
        {
            BeaconBlockHeader header1 = new BeaconBlockHeader();
            header1.Slot = new Slot(1);
            header1.ParentRoot = Sha256.OfAnEmptyString;
            header1.BodyRoot = Sha256.OfAnEmptyString;
            header1.StateRoot = Sha256.OfAnEmptyString;
            header1.Signature = BlsSignature.TestSig1;

            BeaconBlockHeader header2 = new BeaconBlockHeader();
            header2.Slot = new Slot(2);
            header2.ParentRoot = Sha256.OfAnEmptyString;
            header2.BodyRoot = Sha256.OfAnEmptyString;
            header2.StateRoot = Sha256.OfAnEmptyString;
            header2.Signature = BlsSignature.TestSig1;

            ProposerSlashing container = new ProposerSlashing();
            container.ProposerIndex = new ValidatorIndex(1);
            container.Header1 = header1;
            container.Header2 = header2;

            Span<byte> encoded = new byte[ProposerSlashing.SszLength];
            Ssz.Encode(encoded, container);
            ProposerSlashing? decoded = Ssz.DecodeProposerSlashing(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attester_slashing_there_and_back()
        {
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.CommitteeIndex = new CommitteeIndex(2);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            data.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            IndexedAttestation indexedAttestation1 = new IndexedAttestation();
            indexedAttestation1.AttestingIndices = new ValidatorIndex[3];
            indexedAttestation1.Data = data;
            indexedAttestation1.Signature = BlsSignature.TestSig1;

            IndexedAttestation indexedAttestation2 = new IndexedAttestation();
            indexedAttestation2.AttestingIndices = new ValidatorIndex[5];
            indexedAttestation2.Data = data;
            indexedAttestation2.Signature = BlsSignature.TestSig1;

            AttesterSlashing container = new AttesterSlashing();
            container.Attestation1 = indexedAttestation1;
            container.Attestation2 = indexedAttestation2;

            Span<byte> encoded = new byte[AttesterSlashing.SszLength(container)];
            Ssz.Encode(encoded, container);
            AttesterSlashing? decoded = Ssz.DecodeAttesterSlashing(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Attestation_there_and_back()
        {
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.CommitteeIndex = new CommitteeIndex(2);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            data.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            Attestation container = new Attestation();
            container.AggregationBits = new byte[] {1, 2, 3};
            container.Data = data;
            container.Signature = BlsSignature.TestSig1;

            Span<byte> encoded = new byte[Attestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            Attestation? decoded = Ssz.DecodeAttestation(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Deposit_there_and_back()
        {
            DepositData data = new DepositData();
            data.PublicKey = BlsPublicKey.TestKey1;
            data.WithdrawalCredentials = Sha256.OfAnEmptyString;
            data.Amount = Gwei.One;
            data.Signature = BlsSignature.TestSig1;

            Deposit container = new Deposit();
            container.Data = data;
            container.Proof[7] = Sha256.OfAnEmptyString;

            Span<byte> encoded = new byte[Deposit.SszLength];
            Ssz.Encode(encoded, container);
            Deposit? decoded = Ssz.DecodeDeposit(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Voluntary_exit_there_and_back()
        {
            VoluntaryExit container = new VoluntaryExit();
            container.Epoch = new Epoch(1);
            container.ValidatorIndex = new ValidatorIndex(2);
            container.Signature = BlsSignature.TestSig1;

            Span<byte> encoded = new byte[VoluntaryExit.SszLength];
            Ssz.Encode(encoded, container);
            VoluntaryExit? decoded = Ssz.DecodeVoluntaryExit(encoded);
            Assert.AreEqual(container, decoded);
            
            Merkle.Ize(out UInt256 root, container);
        }

        [Test]
        public void Beacon_block_body_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data();
            eth1Data.BlockHash = Sha256.OfAnEmptyString;
            eth1Data.DepositCount = 1;
            eth1Data.DepositRoot = Sha256.OfAnEmptyString;

            BeaconBlockBody container = new BeaconBlockBody();
            container.RandaoReversal = BlsSignature.TestSig1;
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

            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.Source = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString);
            data.CommitteeIndex = new CommitteeIndex(4);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;

            Attestation attestation = new Attestation();
            attestation.Data = data;
            attestation.Signature = BlsSignature.TestSig1;
            attestation.AggregationBits = new byte[5];

            DepositData depositData = new DepositData();
            depositData.Amount = new Gwei(7);
            depositData.Signature = BlsSignature.TestSig1;
            depositData.PublicKey = BlsPublicKey.TestKey1;
            depositData.WithdrawalCredentials = Sha256.OfAnEmptyString;

            Deposit deposit = new Deposit();
            deposit.Data = depositData;
            deposit.Proof = new Hash32[Deposit.ContractTreeDepth + 1];

            IndexedAttestation indexedAttestation1 = new IndexedAttestation();
            indexedAttestation1.Data = data;
            indexedAttestation1.Signature = BlsSignature.TestSig1;
            indexedAttestation1.AttestingIndices = new ValidatorIndex[8];

            IndexedAttestation indexedAttestation2 = new IndexedAttestation();
            indexedAttestation2.Data = data;
            indexedAttestation2.Signature = BlsSignature.TestSig1;
            indexedAttestation2.AttestingIndices = new ValidatorIndex[8];

            AttesterSlashing slashing = new AttesterSlashing();
            slashing.Attestation1 = indexedAttestation1;
            slashing.Attestation2 = indexedAttestation2;

            Eth1Data eth1Data = new Eth1Data();
            eth1Data.BlockHash = Sha256.OfAnEmptyString;
            eth1Data.DepositCount = 9;
            eth1Data.DepositRoot = Sha256.OfAnEmptyString;

            body.Attestations = new Attestation[3];
            body.Attestations[1] = attestation;

            body.Deposits = new Deposit[3];
            body.Deposits[2] = deposit;

            body.Graffiti = new byte[32];
            body.AttesterSlashings = new AttesterSlashing[3];
            body.AttesterSlashings[0] = slashing;
            body.Eth1Data = eth1Data;
            body.ProposerSlashings = new ProposerSlashing[10];
            body.RandaoReversal = BlsSignature.TestSig1;
            body.VoluntaryExits = new VoluntaryExit[11];

            byte[] encoded = new byte[BeaconBlockBody.SszLength(body)];
            Ssz.Encode(encoded, body);
        }

        [Test]
        public void Beacon_block_there_and_back()
        {
            Eth1Data eth1Data = new Eth1Data();
            eth1Data.BlockHash = Sha256.OfAnEmptyString;
            eth1Data.DepositCount = 1;
            eth1Data.DepositRoot = Sha256.OfAnEmptyString;

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody();
            beaconBlockBody.RandaoReversal = BlsSignature.TestSig1;
            beaconBlockBody.Eth1Data = eth1Data;
            beaconBlockBody.Graffiti = new byte[32];
            beaconBlockBody.ProposerSlashings = new ProposerSlashing[2];
            beaconBlockBody.AttesterSlashings = new AttesterSlashing[3];
            beaconBlockBody.Attestations = new Attestation[4];
            beaconBlockBody.Deposits = new Deposit[5];
            beaconBlockBody.VoluntaryExits = new VoluntaryExit[6];

            BeaconBlock container = new BeaconBlock();
            container.Body = beaconBlockBody;
            container.Signature = BlsSignature.TestSig1;
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
            Eth1Data eth1Data = new Eth1Data();
            eth1Data.BlockHash = Sha256.OfAnEmptyString;
            eth1Data.DepositCount = 1;
            eth1Data.DepositRoot = Sha256.OfAnEmptyString;

            BeaconBlockHeader beaconBlockHeader = new BeaconBlockHeader();
            beaconBlockHeader.Signature = BlsSignature.TestSig1;
            beaconBlockHeader.Slot = new Slot(14);
            beaconBlockHeader.BodyRoot = Sha256.OfAnEmptyString;
            beaconBlockHeader.ParentRoot = Sha256.OfAnEmptyString;
            beaconBlockHeader.StateRoot = Sha256.OfAnEmptyString;

            BeaconBlockBody beaconBlockBody = new BeaconBlockBody();
            beaconBlockBody.RandaoReversal = BlsSignature.TestSig1;
            beaconBlockBody.Eth1Data = eth1Data;
            beaconBlockBody.Graffiti = new byte[32];
            beaconBlockBody.ProposerSlashings = new ProposerSlashing[2];
            beaconBlockBody.AttesterSlashings = new AttesterSlashing[3];
            beaconBlockBody.Attestations = new Attestation[4];
            beaconBlockBody.Deposits = new Deposit[5];
            beaconBlockBody.VoluntaryExits = new VoluntaryExit[6];

            BeaconBlock beaconBlock = new BeaconBlock();
            beaconBlock.Body = beaconBlockBody;
            beaconBlock.Signature = BlsSignature.TestSig1;
            beaconBlock.Slot = new Slot(1);
            beaconBlock.ParentRoot = Sha256.OfAnEmptyString;
            beaconBlock.StateRoot = Sha256.OfAnEmptyString;

            BeaconState container = new BeaconState();
            container.Balances = new Gwei[3];
            container.Fork = new Fork(new ForkVersion(5), new ForkVersion(7), new Epoch(3));
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