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
using Nethermind.Core.Extensions;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NUnit.Framework;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class SszContainersTests
    {
        [Test]
        public void Fork_there_and_back()
        {
            Fork container = new Fork(new ForkVersion(1), new ForkVersion(2), new Epoch(3));
            Span<byte> encoded = stackalloc byte[Fork.SszLength];
            Ssz.Encode(encoded, container);
            Fork decoded = Ssz.DecodeFork(encoded);
            Assert.AreEqual(container, decoded);
        }

        [Test]
        public void Checkpoint_there_and_back()
        {
            Checkpoint container = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            Span<byte> encoded = stackalloc byte[Checkpoint.SszLength];
            Ssz.Encode(encoded, container);
            Checkpoint decoded = Ssz.DecodeCheckpoint(encoded);
            Assert.AreEqual(container, decoded);
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

            Span<byte> encoded = stackalloc byte[Validator.SszLength];
            Ssz.Encode(encoded, container);
            Validator decoded = Ssz.DecodeValidator(encoded);
            Assert.AreEqual(container, decoded);
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

            Span<byte> encoded = stackalloc byte[AttestationData.SszLength];
            Ssz.Encode(encoded, container);
            AttestationData decoded = Ssz.DecodeAttestationData(encoded);
            Assert.AreEqual(container, decoded);
            
            Span<byte> encodedAgain = stackalloc byte[AttestationData.SszLength];
            Ssz.Encode(encodedAgain, decoded);
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
        }

        [Test]
        public void Attestation_data_and_custody_bit_there_and_back()
        {
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.CommitteeIndex = new CommitteeIndex(2);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            data.Source = new Checkpoint(new Epoch(1), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);

            AttestationDataAndCustodyBit container = new AttestationDataAndCustodyBit();
            container.Data = data;
            container.CustodyBit = true;

            Span<byte> encoded = stackalloc byte[AttestationDataAndCustodyBit.SszLength];
            Ssz.Encode(encoded, container);
            AttestationDataAndCustodyBit decoded = Ssz.DecodeAttestationDataAndCustodyBit(encoded);
            Assert.AreEqual(container, decoded);
            
            
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
            container.CustodyBit0Indices = new ValidatorIndex[3];
            container.CustodyBit1Indices = new ValidatorIndex[7];
            container.Data = data;
            container.Signature = BlsSignature.TestSig1;
            
            Span<byte> encoded = stackalloc byte[IndexedAttestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            IndexedAttestation decoded = Ssz.DecodeIndexedAttestation(encoded);
            Assert.AreEqual(container, decoded);
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
            
            Span<byte> encoded = stackalloc byte[PendingAttestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            PendingAttestation decoded = Ssz.DecodePendingAttestation(encoded);
            Assert.AreEqual(container, decoded);
        }

        [Test]
        public void Eth1_data_there_and_back()
        {
            Eth1Data container = new Eth1Data();
            container.BlockHash = Sha256.OfAnEmptyString;
            container.DepositCount = 1;
            container.DepositRoot = Sha256.OfAnEmptyString;
            Span<byte> encoded = stackalloc byte[Eth1Data.SszLength];
            Ssz.Encode(encoded, container);
            Eth1Data decoded = Ssz.DecodeEth1Data(encoded);
            Assert.AreEqual(container, decoded);
        }

        [Test]
        public void Historical_batch_there_and_back()
        {
            HistoricalBatch container = new HistoricalBatch();
            container.BlockRoots[3] = Sha256.OfAnEmptyString;
            container.StateRoots[7] = Sha256.OfAnEmptyString;
            Span<byte> encoded = stackalloc byte[HistoricalBatch.SszLength];
            Ssz.Encode(encoded, container);
            HistoricalBatch decoded = Ssz.DecodeHistoricalBatch(encoded);
            Assert.AreEqual(container, decoded);
        }

        [Test]
        public void Deposit_data_there_and_back()
        {
            DepositData container = new DepositData();
            container.PublicKey = BlsPublicKey.TestKey1;
            container.WithdrawalCredentials = Sha256.OfAnEmptyString;
            container.Amount = Gwei.One;
            container.Signature = BlsSignature.TestSig1;
            Span<byte> encoded = stackalloc byte[DepositData.SszLength];
            Ssz.Encode(encoded, container);
            DepositData decoded = Ssz.DecodeDepositData(encoded);
            Assert.AreEqual(container, decoded);
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
            Span<byte> encoded = stackalloc byte[BeaconBlockHeader.SszLength];
            Ssz.Encode(encoded, container);
            BeaconBlockHeader decoded = Ssz.DecodeBeaconBlockHeader(encoded);
            Assert.AreEqual(container, decoded);
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
            
            Span<byte> encoded = stackalloc byte[ProposerSlashing.SszLength];
            Ssz.Encode(encoded, container);
            ProposerSlashing decoded = Ssz.DecodeProposerSlashing(encoded);
            Assert.AreEqual(container, decoded);
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
            indexedAttestation1.CustodyBit0Indices = new ValidatorIndex[3];
            indexedAttestation1.CustodyBit1Indices = new ValidatorIndex[7];
            indexedAttestation1.Data = data;
            indexedAttestation1.Signature = BlsSignature.TestSig1;
            
            IndexedAttestation indexedAttestation2 = new IndexedAttestation();
            indexedAttestation2.CustodyBit0Indices = new ValidatorIndex[5];
            indexedAttestation2.CustodyBit1Indices = new ValidatorIndex[11];
            indexedAttestation2.Data = data;
            indexedAttestation2.Signature = BlsSignature.TestSig1;
            
            AttesterSlashing container = new AttesterSlashing();
            container.Attestation1 = indexedAttestation1;
            container.Attestation2 = indexedAttestation2;
            
            Span<byte> encoded = stackalloc byte[AttesterSlashing.SszLength(container)];
            Ssz.Encode(encoded, container);
            AttesterSlashing decoded = Ssz.DecodeAttesterSlashing(encoded);
            Assert.AreEqual(container, decoded);
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
            container.AggregationBits = new byte[3];
            container.Data = data;
            container.CustodyBits = new byte[7];
            container.Signature = BlsSignature.TestSig1;
            
            Span<byte> encoded = stackalloc byte[Attestation.SszLength(container)];
            Ssz.Encode(encoded, container);
            Attestation decoded = Ssz.DecodeAttestation(encoded);
            Assert.AreEqual(container, decoded);
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

            Span<byte> encoded = stackalloc byte[Deposit.SszLength];
            Ssz.Encode(encoded, container);
            Deposit decoded = Ssz.DecodeDeposit(encoded);
            Assert.AreEqual(container, decoded);
        }

        [Test]
        public void Voluntary_exit_there_and_back()
        {
            VoluntaryExit container = new VoluntaryExit();
            container.Epoch = new Epoch(1);
            container.ValidatorIndex = new ValidatorIndex(2);
            container.Signature = BlsSignature.TestSig1;

            Span<byte> encoded = stackalloc byte[VoluntaryExit.SszLength];
            Ssz.Encode(encoded, container);
            VoluntaryExit decoded = Ssz.DecodeVoluntaryExit(encoded);
            Assert.AreEqual(container, decoded);
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
            
            Span<byte> encoded = stackalloc byte[BeaconBlockBody.SszLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlockBody decoded = Ssz.DecodeBeaconBlockBody(encoded);
            Assert.AreEqual(container, decoded);
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
            
            Span<byte> encoded = stackalloc byte[BeaconBlock.SszLength(container)];
            Ssz.Encode(encoded, container);
            BeaconBlock decoded = Ssz.DecodeBeaconBlock(encoded);
            Assert.AreEqual(container, decoded);
            
            Span<byte> encodedAgain = stackalloc byte[BeaconBlock.SszLength(container)];
            Ssz.Encode(encodedAgain, decoded);
            Assert.True(Bytes.AreEqual(encodedAgain, encoded));
        }

        [Test]
        public void Beacon_state_there_and_back()
        {
            throw new NotSupportedException();
        }
    }
}