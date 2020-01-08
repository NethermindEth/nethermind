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

using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public static class ByteLength
    {
        public const int SlotLength = sizeof(ulong);
        public const int EpochLength = sizeof(ulong);
        public const int CommitteeIndexLength = sizeof(ulong);
        public const int GweiLength = sizeof(ulong);
        public const int ValidatorIndexLength = sizeof(ulong);
        public const int Hash32Length = Hash32.Length;
        public const int ForkVersionLength = ForkVersion.Length;
        public const int BlsPublicKeyLength = BlsPublicKey.Length;
        public const int BlsSignatureLength = BlsSignature.Length;
        public const int ForkLength = ByteLength.ForkVersionLength * 2 + ByteLength.EpochLength;
        public const int CheckpointLength = ByteLength.Hash32Length + ByteLength.EpochLength;
        public const int ValidatorLength = ByteLength.BlsPublicKeyLength + ByteLength.Hash32Length + ByteLength.GweiLength + 1 + 4 * ByteLength.EpochLength;
        public const int AttestationDataLength = ByteLength.SlotLength + ByteLength.CommitteeIndexLength + ByteLength.Hash32Length + 2 * ByteLength.CheckpointLength;

        public const int IndexedAttestationDynamicOffset = sizeof(uint) +
                                            ByteLength.AttestationDataLength +
                                            ByteLength.BlsSignatureLength;

        public static int IndexedAttestationLength(IndexedAttestation? value)
        {
            if (value is null)
            {
                return 0;
            }
            
            return ByteLength.IndexedAttestationDynamicOffset +
                   (value.AttestingIndices?.Count ?? 0) * ByteLength.ValidatorIndexLength;
        }

        public const int PendingAttestationDynamicOffset = sizeof(uint) +
                                                           ByteLength.AttestationDataLength +
                                                           ByteLength.SlotLength +
                                                           ByteLength.ValidatorIndexLength;

        public static int PendingAttestationLength(PendingAttestation? value)
        {
            if (value == null)
            {
                return 0;
            }
            
            // TODO: AggregationBits is a Bitlist, not Bitvector, so needs a sentinel '1' at the end, i.e. byte length is (Len+8)/8
            return ByteLength.PendingAttestationDynamicOffset + (value.AggregationBits.Length + 7) / 8;
        }

        public const int Eth1DataLength = 2 * ByteLength.Hash32Length + sizeof(ulong);
        public static int HistoricalBatchLength = 2 * Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length;
        public const int DepositDataLength = ByteLength.BlsPublicKeyLength + ByteLength.Hash32Length + ByteLength.GweiLength + ByteLength.BlsSignatureLength;
        public const int BeaconBlockHeaderLength = ByteLength.SlotLength + 3 * ByteLength.Hash32Length + ByteLength.BlsSignatureLength;
        public const int AttestationDynamicOffset = sizeof(uint) + ByteLength.AttestationDataLength + ByteLength.BlsSignatureLength;
        public static readonly uint MaxValidatorsPerCommittee = 2048;

        public static int AttestationLength(Attestation? container)
        {
            if (container == null)
            {
                return 0;
            }

            // TODO: Need to include Bitlist final 1 bit
            return AttestationDynamicOffset + (container.AggregationBits.Length + 7) / 8;
        }

        public static int AttesterSlashingLength(AttesterSlashing? container)
        {
            if (container is null)
            {
                return 0;
            }
            
            return 2 * sizeof(uint) +
                   ByteLength.IndexedAttestationLength(container.Attestation1) +
                   ByteLength.IndexedAttestationLength(container.Attestation2);
        }

        public const int ContractTreeDepth = 32;
        public const int DepositLengthOfProof = (ContractTreeDepth + 1) * ByteLength.Hash32Length;
        public const int DepositLength = DepositLengthOfProof + ByteLength.DepositDataLength;
        public const int ProposerSlashingLength = ByteLength.ValidatorIndexLength + 2 * ByteLength.BeaconBlockHeaderLength;
        public const int VoluntaryExitLength = ByteLength.EpochLength + ByteLength.ValidatorIndexLength + ByteLength.BlsSignatureLength;
        public const int BeaconBlockBodyDynamicOffset = ByteLength.BlsSignatureLength + ByteLength.Eth1DataLength + 32 + 5 * sizeof(uint);

        public static int BeaconBlockBodyLength(BeaconBlockBody? container)
        {
            if (container is null)
            {
                return 0;
            }

            int result = BeaconBlockBodyDynamicOffset;

            result += ByteLength.ProposerSlashingLength * container.ProposerSlashings.Count;
            result += ByteLength.DepositLength * container.Deposits.Count;
            result += ByteLength.VoluntaryExitLength * container.VoluntaryExits.Count;

            result += sizeof(uint) * container.AttesterSlashings.Count;
            for (int i = 0; i < container.AttesterSlashings.Count; i++)
            {
                result += ByteLength.AttesterSlashingLength(container.AttesterSlashings[i]);
            }

            result += sizeof(uint) * container.Attestations.Count;
            for (int i = 0; i < container.Attestations.Count; i++)
            {
                result += ByteLength.AttestationLength(container.Attestations[i]);
            }

            return result;
        }

        public const int BeaconBlockDynamicOffset = ByteLength.SlotLength + 2 * ByteLength.Hash32Length + sizeof(uint) + ByteLength.BlsSignatureLength;

        public static int BeaconBlockLength(BeaconBlock? container)
        {
            return container is null ? 0 : (BeaconBlockDynamicOffset + ByteLength.BeaconBlockBodyLength(container.Body));
        }

        public static uint MaxProposerSlashings { get; set; } = 16;
        public static uint MaxAttesterSlashings { get; set; } = 1;
        public static uint MaxAttestations { get; set; } = 128;
        public static uint MaxDeposits { get; set; } = 16;
        public static uint MaxVoluntaryExits { get; set; } = 16;

        public static int BeaconStateDynamicOffset = sizeof(ulong) +
                                                     Core2.ByteLength.SlotLength +
                                                     ByteLength.ForkLength +
                                                     ByteLength.BeaconBlockHeaderLength +
                                                     2 * Time.SlotsPerHistoricalRoot * ByteLength.Hash32Length +
                                                     sizeof(uint) +
                                                     ByteLength.Eth1DataLength +
                                                     sizeof(uint) +
                                                     sizeof(ulong) +
                                                     2 * sizeof(uint) +
                                                     Time.EpochsPerHistoricalVector * ByteLength.Hash32Length +
                                                     Time.EpochsPerSlashingsVector * ByteLength.GweiLength +
                                                     2 * sizeof(uint) +
                                                     1 + // not sure
                                                     3 * ByteLength.CheckpointLength;

        public const ulong HistoricalRootsLimit = 16_777_216;

        public static int BeaconStateLength(BeaconState? container)
        {
            if (container is null)
            {
                return 0;
            }
            
            int result = BeaconStateDynamicOffset;
            result += ByteLength.Hash32Length * (container.HistoricalRoots?.Count ?? 0);
            result += ByteLength.ValidatorLength * (container.Validators?.Count ?? 0);
            result += ByteLength.GweiLength * (container.Balances?.Count ?? 0);
            result += ByteLength.Eth1DataLength * (container.Eth1DataVotes?.Count ?? 0);

            result += (container.PreviousEpochAttestations?.Count ?? 0) * sizeof(uint);
            if (!(container.PreviousEpochAttestations is null))
            {
                for (int i = 0; i < container.PreviousEpochAttestations.Count; i++)
                {
                    result += ByteLength.PendingAttestationLength(container.PreviousEpochAttestations[i]);
                }
            }

            result += (container.CurrentEpochAttestations?.Count ?? 0) * sizeof(uint);
            if (!(container.CurrentEpochAttestations is null))
            {
                for (int i = 0; i < container.CurrentEpochAttestations.Count; i++)
                {
                    result += ByteLength.PendingAttestationLength(container.CurrentEpochAttestations[i]);
                }
            }

            return result;
        }
    }
}