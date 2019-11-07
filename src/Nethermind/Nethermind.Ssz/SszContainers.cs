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
using System.Buffers.Binary;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Newtonsoft.Json.Converters;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static void Encode(Span<byte> span, Fork container)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidTargetLength<Fork>(span.Length, Fork.SszLength);
            }

            Encode(span.Slice(0, ForkVersion.SszLength), container.PreviousVersion);
            Encode(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength), container.CurrentVersion);
            Encode(span.Slice(2 * ForkVersion.SszLength), container.Epoch);
        }

        public static Fork DecodeFork(Span<byte> span)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidSourceLength<Fork>(span.Length, Fork.SszLength);
            }

            ForkVersion previous = DecodeForkVersion(span.Slice(0, ForkVersion.SszLength));
            ForkVersion current = DecodeForkVersion(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength));
            Epoch epoch = DecodeEpoch(span.Slice(2 * ForkVersion.SszLength));

            return new Fork(previous, current, epoch);
        }

        public static void Encode(Span<byte> span, Checkpoint container)
        {
            if (span.Length != Checkpoint.SszLength)
            {
                ThrowInvalidTargetLength<Checkpoint>(span.Length, Checkpoint.SszLength);
            }

            Encode(span.Slice(0, Epoch.SszLength), container.Epoch);
            Encode(span.Slice(Epoch.SszLength), container.Root);
        }

        public static Checkpoint DecodeCheckpoint(Span<byte> span)
        {
            if (span.Length != Checkpoint.SszLength)
            {
                ThrowInvalidSourceLength<Checkpoint>(span.Length, Checkpoint.SszLength);
            }

            Epoch epoch = DecodeEpoch(span.Slice(0, Epoch.SszLength));
            Sha256 root = DecodeSha256(span.Slice(Epoch.SszLength));

            return new Checkpoint(epoch, root);
        }

        public static void Encode(Span<byte> span, Validator container)
        {
            if (span.Length != Validator.SszLength)
            {
                ThrowInvalidTargetLength<Validator>(span.Length, Validator.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, BlsPublicKey.SszLength), container.PublicKey);
            offset += BlsPublicKey.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.WithdrawalCredentials);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Gwei.SszLength), container.EffectiveBalance);
            offset += Gwei.SszLength;
            Encode(span.Slice(offset, 1), container.Slashed);
            offset += 1;
            Encode(span.Slice(offset, Epoch.SszLength), container.ActivationEligibilityEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset, Epoch.SszLength), container.ActivationEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset, Epoch.SszLength), container.ExitEpoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset), container.WithdrawableEpoch);
        }

        public static Validator DecodeValidator(Span<byte> span)
        {
            if (span.Length != Validator.SszLength)
            {
                ThrowInvalidSourceLength<Validator>(span.Length, Validator.SszLength);
            }

            int offset = 0;
            BlsPublicKey publicKey = DecodeBlsPublicKey(span.Slice(offset, BlsPublicKey.SszLength));
            Validator container = new Validator(publicKey);
            offset += BlsPublicKey.SszLength;
            container.WithdrawalCredentials = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.EffectiveBalance = DecodeGwei(span.Slice(offset, Gwei.SszLength));
            offset += Gwei.SszLength;
            container.Slashed = DecodeBool(span.Slice(offset, 1));
            offset += 1;
            container.ActivationEligibilityEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.ActivationEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.ExitEpoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.WithdrawableEpoch = DecodeEpoch(span.Slice(offset));

            return container;
        }

        public static void Encode(Span<byte> span, AttestationData container)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowInvalidTargetLength<AttestationData>(span.Length, AttestationData.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, Slot.SszLength), container.Slot);
            offset += Slot.SszLength;
            Encode(span.Slice(offset, CommitteeIndex.SszLength), container.CommitteeIndex);
            offset += CommitteeIndex.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.BeaconBlockRoot);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Checkpoint.SszLength), container.Source);
            offset += Checkpoint.SszLength;
            Encode(span.Slice(offset, Checkpoint.SszLength), container.Target);
        }

        public static AttestationData DecodeAttestationData(Span<byte> span)
        {
            if (span.Length != AttestationData.SszLength)
            {
                ThrowInvalidSourceLength<AttestationData>(span.Length, AttestationData.SszLength);
            }


            AttestationData container = new AttestationData();
            int offset = 0;
            container.Slot = DecodeSlot(span.Slice(offset, Slot.SszLength));
            offset += Slot.SszLength;
            container.CommitteeIndex = DecodeCommitteeIndex(span.Slice(offset, CommitteeIndex.SszLength));
            offset += CommitteeIndex.SszLength;
            container.BeaconBlockRoot = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.Source = DecodeCheckpoint(span.Slice(offset, Checkpoint.SszLength));
            offset += Checkpoint.SszLength;
            container.Target = DecodeCheckpoint(span.Slice(offset, Checkpoint.SszLength));
            return container;
        }

        public static void Encode(Span<byte> span, AttestationDataAndCustodyBit container)
        {
            if (span.Length != AttestationDataAndCustodyBit.SszLength)
            {
                ThrowInvalidTargetLength<AttestationDataAndCustodyBit>(span.Length, AttestationDataAndCustodyBit.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, AttestationData.SszLength), container.Data);
            offset += AttestationData.SszLength;
            Encode(span.Slice(offset, 1), container.CustodyBit);
        }

        public static AttestationDataAndCustodyBit DecodeAttestationDataAndCustodyBit(Span<byte> span)
        {
            if (span.Length != AttestationDataAndCustodyBit.SszLength)
            {
                ThrowInvalidSourceLength<AttestationDataAndCustodyBit>(span.Length, AttestationDataAndCustodyBit.SszLength);
            }

            AttestationDataAndCustodyBit container = new AttestationDataAndCustodyBit();
            int offset = 0;
            container.Data = DecodeAttestationData(span.Slice(offset, AttestationData.SszLength));
            offset += AttestationData.SszLength;
            container.CustodyBit = DecodeBool(span.Slice(offset, 1));
            return container;
        }

        public static void Encode(Span<byte> span, IndexedAttestation container)
        {
            if (span.Length != IndexedAttestation.SszLength(container))
            {
                ThrowInvalidTargetLength<IndexedAttestation>(span.Length, IndexedAttestation.SszLength(container));
            }

            int offset = 0;
            int dynamicOffset = 2 * sizeof(uint) + AttestationData.SszLength + BlsSignature.SszLength;
            int lengthBits0 = container.CustodyBit0Indices.Length * ValidatorIndex.SszLength;
            Encode(span.Slice(offset, sizeof(uint)), dynamicOffset);
            Encode(span.Slice(dynamicOffset, lengthBits0), container.CustodyBit0Indices);
            offset += sizeof(uint);
            dynamicOffset += lengthBits0;
            Encode(span.Slice(offset, sizeof(uint)), dynamicOffset);
            Encode(span.Slice(dynamicOffset), container.CustodyBit1Indices);
            offset += sizeof(uint);
            Encode(span.Slice(offset, AttestationData.SszLength), container.Data);
            offset += AttestationData.SszLength;
            Encode(span.Slice(offset, BlsSignature.SszLength), container.Signature);
        }

        public static IndexedAttestation DecodeIndexedAttestation(Span<byte> span)
        {
            IndexedAttestation container = new IndexedAttestation();
            uint bits0Offset = DecodeUInt(span.Slice(0, sizeof(uint)));
            uint bits1Offset = DecodeUInt(span.Slice(sizeof(uint), sizeof(uint)));

            uint bits0Length = bits1Offset - bits0Offset;
            uint bits1Length = (uint) span.Length - bits1Offset;

            container.CustodyBit0Indices = DecodeValidatorIndexes(span.Slice((int) bits0Offset, (int) bits0Length));
            container.CustodyBit1Indices = DecodeValidatorIndexes(span.Slice((int) bits1Offset, (int) bits1Length));
            container.Data = DecodeAttestationData(span.Slice(2 * sizeof(uint), AttestationData.SszLength));
            container.Signature = DecodeBlsSignature(span.Slice(2 * sizeof(uint) + AttestationData.SszLength, BlsSignature.SszLength));

            return container;
        }

        public static void Encode(Span<byte> span, PendingAttestation container)
        {
            if (span.Length != PendingAttestation.SszLength(container))
            {
                ThrowInvalidTargetLength<PendingAttestation>(span.Length, PendingAttestation.SszLength(container));
            }
        }

        public static PendingAttestation DecodePendingAttestation(Span<byte> span)
        {
            return new PendingAttestation();
        }

        public static void Encode(Span<byte> span, Eth1Data container)
        {
            if (span.Length != Eth1Data.SszLength)
            {
                ThrowInvalidTargetLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            }

            Encode(span.Slice(0, Sha256.SszLength), container.DepositRoot);
            Encode(span.Slice(Sha256.SszLength, sizeof(ulong)), container.DepositCount);
            Encode(span.Slice(Sha256.SszLength + sizeof(ulong)), container.BlockHash);
        }

        public static Eth1Data DecodeEth1Data(Span<byte> span)
        {
            if (span.Length != Eth1Data.SszLength)
            {
                ThrowInvalidSourceLength<Eth1Data>(span.Length, Eth1Data.SszLength);
            }

            Eth1Data container = new Eth1Data();
            container.DepositRoot = DecodeSha256(span.Slice(0, Sha256.SszLength));
            container.DepositCount = DecodeULong(span.Slice(Sha256.SszLength, sizeof(ulong)));
            container.BlockHash = DecodeSha256(span.Slice(Sha256.SszLength + sizeof(ulong), Sha256.SszLength));
            return container;
        }

        public static void Encode(Span<byte> span, HistoricalBatch container)
        {
            if (span.Length != HistoricalBatch.SszLength)
            {
                ThrowInvalidTargetLength<HistoricalBatch>(span.Length, HistoricalBatch.SszLength);
            }

            Encode(span.Slice(0, HistoricalBatch.SszLength / 2), container.BlockRoots);
            Encode(span.Slice(HistoricalBatch.SszLength / 2), container.StateRoots);
        }

        public static HistoricalBatch DecodeHistoricalBatch(Span<byte> span)
        {
            if (span.Length != HistoricalBatch.SszLength)
            {
                ThrowInvalidSourceLength<HistoricalBatch>(span.Length, HistoricalBatch.SszLength);
            }

            HistoricalBatch container = new HistoricalBatch();
            container.BlockRoots = DecodeHashes(span.Slice(0, HistoricalBatch.SszLength / 2));
            container.StateRoots = DecodeHashes(span.Slice(HistoricalBatch.SszLength / 2));
            return container;
        }

        public static void Encode(Span<byte> span, DepositData container)
        {
            if (span.Length != DepositData.SszLength)
            {
                ThrowInvalidTargetLength<DepositData>(span.Length, DepositData.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, BlsPublicKey.SszLength), container.PublicKey);
            offset += BlsPublicKey.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.WithdrawalCredentials);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Gwei.SszLength), container.Amount);
            offset += Gwei.SszLength;
            Encode(span.Slice(offset, BlsSignature.SszLength), container.Signature);
        }

        public static DepositData DecodeDepositData(Span<byte> span)
        {
            if (span.Length != DepositData.SszLength)
            {
                ThrowInvalidSourceLength<DepositData>(span.Length, DepositData.SszLength);
            }

            DepositData container = new DepositData();
            int offset = 0;
            container.PublicKey = DecodeBlsPublicKey(span.Slice(0, BlsPublicKey.SszLength));
            offset += BlsPublicKey.SszLength;
            container.WithdrawalCredentials = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.Amount = DecodeGwei(span.Slice(offset, Gwei.SszLength));
            offset += Gwei.SszLength;
            container.Signature = DecodeBlsSignature(span.Slice(offset, BlsSignature.SszLength));
            return container;
        }

        public static void Encode(Span<byte> span, BeaconBlockHeader container)
        {
            if (span.Length != BeaconBlockHeader.SszLength)
            {
                ThrowInvalidTargetLength<BeaconBlockHeader>(span.Length, BeaconBlockHeader.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, Slot.SszLength), container.Slot);
            offset += Slot.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.ParentRoot);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.StateRoot);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, Sha256.SszLength), container.BodyRoot);
            offset += Sha256.SszLength;
            Encode(span.Slice(offset, BlsSignature.SszLength), container.Signature);
        }

        public static BeaconBlockHeader DecodeBeaconBlockHeader(Span<byte> span)
        {
            if (span.Length != BeaconBlockHeader.SszLength)
            {
                ThrowInvalidSourceLength<BeaconBlockHeader>(span.Length, BeaconBlockHeader.SszLength);
            }

            BeaconBlockHeader container = new BeaconBlockHeader();
            int offset = 0;
            container.Slot = DecodeSlot(span.Slice(0, Slot.SszLength));
            offset += Slot.SszLength;
            container.ParentRoot = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.StateRoot = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.BodyRoot = DecodeSha256(span.Slice(offset, Sha256.SszLength));
            offset += Sha256.SszLength;
            container.Signature = DecodeBlsSignature(span.Slice(offset, BlsSignature.SszLength));
            return container;
        }

        public static void Encode(Span<byte> span, ProposerSlashing container)
        {
            if (span.Length != ProposerSlashing.SszLength)
            {
                ThrowInvalidTargetLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(0, ValidatorIndex.SszLength), container.ProposerIndex);
            offset += ValidatorIndex.SszLength;
            Encode(span.Slice(offset, BeaconBlockHeader.SszLength), container.Header1);
            offset += BeaconBlockHeader.SszLength;
            Encode(span.Slice(offset, BeaconBlockHeader.SszLength), container.Header2);
        }

        public static ProposerSlashing DecodeProposerSlashing(Span<byte> span)
        {
            if (span.Length != ProposerSlashing.SszLength)
            {
                ThrowInvalidSourceLength<ProposerSlashing>(span.Length, ProposerSlashing.SszLength);
            }

            ProposerSlashing container = new ProposerSlashing();
            int offset = 0;
            container.ProposerIndex = DecodeValidatorIndex(span.Slice(0, ValidatorIndex.SszLength));
            offset += ValidatorIndex.SszLength;
            container.Header1 = DecodeBeaconBlockHeader(span.Slice(offset, BeaconBlockHeader.SszLength));
            offset += BeaconBlockHeader.SszLength;
            container.Header2 = DecodeBeaconBlockHeader(span.Slice(offset, BeaconBlockHeader.SszLength));
            return container;
        }

        public static void Encode(Span<byte> span, AttesterSlashing container)
        {
            if (span.Length != AttesterSlashing.SszLength(container))
            {
                ThrowInvalidTargetLength<AttesterSlashing>(span.Length, AttesterSlashing.SszLength(container));
            }

            int dynamicOffset = 2 * sizeof(uint);
            int length1 = IndexedAttestation.SszLength(container.Attestation1);
            Encode(span.Slice(0, sizeof(uint)), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length1), container.Attestation1);

            dynamicOffset += IndexedAttestation.SszLength(container.Attestation1);
            int length2 = IndexedAttestation.SszLength(container.Attestation2);
            Encode(span.Slice(sizeof(uint), sizeof(uint)), dynamicOffset);
            Encode(span.Slice(dynamicOffset, length2), container.Attestation2);
        }

        public static AttesterSlashing DecodeAttesterSlashing(Span<byte> span)
        {
            AttesterSlashing attesterSlashing = new AttesterSlashing();
            int offset1 = (int) DecodeUInt(span.Slice(0, sizeof(uint)));
            int offset2 = (int) DecodeUInt(span.Slice(sizeof(uint), sizeof(uint)));

            int length1 = offset2 - offset1;
            int length2 = span.Length - offset2;

            attesterSlashing.Attestation1 = DecodeIndexedAttestation(span.Slice(offset1, length1));
            attesterSlashing.Attestation2 = DecodeIndexedAttestation(span.Slice(offset2, length2));

            return attesterSlashing;
        }

        public static void Encode(Span<byte> span, Attestation container)
        {
            if (span.Length != Attestation.SszLength(container))
            {
                ThrowInvalidTargetLength<Attestation>(span.Length, Attestation.SszLength(container));
            }
        }

        public static Attestation DecodeAttestation(Span<byte> span)
        {
            return new Attestation();
        }

        public static void Encode(Span<byte> span, Deposit container)
        {
            if (span.Length != Deposit.SszLength)
            {
                ThrowInvalidTargetLength<Deposit>(span.Length, Deposit.SszLength);
            }

            Encode(span.Slice(0, Deposit.SszLengthOfProof), container.Proof);
            Encode(span.Slice(Deposit.SszLengthOfProof), container.Data);
        }

        public static Deposit DecodeDeposit(Span<byte> span)
        {
            if (span.Length != Deposit.SszLength)
            {
                ThrowInvalidSourceLength<Deposit>(span.Length, Deposit.SszLength);
            }

            Deposit deposit = new Deposit();
            deposit.Proof = DecodeHashes(span.Slice(0, Deposit.SszLengthOfProof));
            deposit.Data = DecodeDepositData(span.Slice(Deposit.SszLengthOfProof));
            return deposit;
        }

        public static void Encode(Span<byte> span, VoluntaryExit container)
        {
            if (span.Length != VoluntaryExit.SszLength)
            {
                ThrowInvalidTargetLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            }

            int offset = 0;
            Encode(span.Slice(offset, Epoch.SszLength), container.Epoch);
            offset += Epoch.SszLength;
            Encode(span.Slice(offset, ValidatorIndex.SszLength), container.ValidatorIndex);
            offset += ValidatorIndex.SszLength;
            Encode(span.Slice(offset, BlsSignature.SszLength), container.Signature);
        }

        public static VoluntaryExit DecodeVoluntaryExit(Span<byte> span)
        {
            if (span.Length != VoluntaryExit.SszLength)
            {
                ThrowInvalidSourceLength<VoluntaryExit>(span.Length, VoluntaryExit.SszLength);
            }

            VoluntaryExit container = new VoluntaryExit();
            int offset = 0;
            container.Epoch = DecodeEpoch(span.Slice(offset, Epoch.SszLength));
            offset += Epoch.SszLength;
            container.ValidatorIndex = DecodeValidatorIndex(span.Slice(offset, ValidatorIndex.SszLength));
            offset += ValidatorIndex.SszLength;
            container.Signature = DecodeBlsSignature(span.Slice(offset));
            return container;
        }

        public static void Encode(Span<byte> span, BeaconBlockBody container)
        {
            if (span.Length != BeaconBlockBody.SszLength(container))
            {
                ThrowInvalidTargetLength<BeaconBlockBody>(span.Length, BeaconBlockBody.SszLength(container));
            }
        }

        public static BeaconBlockBody DecodeBeaconBlockBody(Span<byte> span)
        {
            return new BeaconBlockBody();
        }

        public static void Encode(Span<byte> span, BeaconBlock container)
        {
            if (span.Length != BeaconBlock.SszLength(container))
            {
                ThrowInvalidTargetLength<BeaconBlock>(span.Length, BeaconBlock.SszLength(container));
            }
        }

        public static BeaconBlock DecodeBeaconBlock(Span<byte> span)
        {
            return new BeaconBlock();
        }

        public static void Encode(Span<byte> span, BeaconState container)
        {
            if (span.Length != BeaconState.SszLength(container))
            {
                ThrowInvalidTargetLength<BeaconState>(span.Length, BeaconState.SszLength(container));
            }
        }

        public static BeaconState DecodeBeaconState(Span<byte> span)
        {
            return new BeaconState();
        }
    }
}