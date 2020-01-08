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
using System;

namespace Nethermind.Ssz
{
    public partial class Ssz
    {
        public static ulong ValidatorRegistryLimit = 1_099_511_627_776;
        public static uint SlotsPerEpoch = 32;
        public static int SlotsPerEth1VotingPeriod = 1024;
        public static int SlotsPerHistoricalRoot = 8192;
        public static int EpochsPerHistoricalVector = 65536;
        public static int EpochsPerSlashingsVector = 8192;
        public static int JustificationBitsLength = 4;
        public static uint MaxValidatorsPerCommittee = 2048;
        public static uint MaxProposerSlashings { get; set; } = 16;
        public static uint MaxAttesterSlashings { get; set; } = 1;
        public static uint MaxAttestations { get; set; } = 128;
        public static uint MaxDeposits { get; set; } = 16;
        public static uint MaxVoluntaryExits { get; set; } = 16;        
        public static ulong HistoricalRootsLimit = 16_777_216;
        
        
        // lengths
        
        public const int SlotLength = sizeof(ulong);
        public const int EpochLength = sizeof(ulong);
        public const int CommitteeIndexLength = sizeof(ulong);
        public const int GweiLength = sizeof(ulong);
        public const int ValidatorIndexLength = sizeof(ulong);
        public const int Hash32Length = Hash32.Length;
        public const int ForkVersionLength = ForkVersion.Length;
        public const int BlsPublicKeyLength = BlsPublicKey.Length;
        public const int BlsSignatureLength = BlsSignature.Length;
        public const int ForkLength = Ssz.ForkVersionLength * 2 + Ssz.EpochLength;
        public const int CheckpointLength = Ssz.Hash32Length + Ssz.EpochLength;
        public const int ValidatorLength = Ssz.BlsPublicKeyLength + Ssz.Hash32Length + Ssz.GweiLength + 1 + 4 * Ssz.EpochLength;
        public const int AttestationDataLength = Ssz.SlotLength + Ssz.CommitteeIndexLength + Ssz.Hash32Length + 2 * Ssz.CheckpointLength;

        public const int IndexedAttestationDynamicOffset = sizeof(uint) +
                                            Ssz.AttestationDataLength +
                                            Ssz.BlsSignatureLength;

        public static int IndexedAttestationLength(IndexedAttestation? value)
        {
            if (value is null)
            {
                return 0;
            }
            
            return Ssz.IndexedAttestationDynamicOffset +
                   (value.AttestingIndices?.Count ?? 0) * Ssz.ValidatorIndexLength;
        }

        public const int PendingAttestationDynamicOffset = sizeof(uint) +
                                                           Ssz.AttestationDataLength +
                                                           Ssz.SlotLength +
                                                           Ssz.ValidatorIndexLength;

        public static int PendingAttestationLength(PendingAttestation? value)
        {
            if (value == null)
            {
                return 0;
            }
            
            return Ssz.PendingAttestationDynamicOffset + (value.AggregationBits.Length + 8) / 8;
        }

        public const int Eth1DataLength = 2 * Ssz.Hash32Length + sizeof(ulong);
        public static int HistoricalBatchLength = 2 * SlotsPerHistoricalRoot * Ssz.Hash32Length;
        public const int DepositDataLength = Ssz.BlsPublicKeyLength + Ssz.Hash32Length + Ssz.GweiLength + Ssz.BlsSignatureLength;
        public const int BeaconBlockHeaderLength = Ssz.SlotLength + 3 * Ssz.Hash32Length + Ssz.BlsSignatureLength;
        public const int AttestationDynamicOffset = sizeof(uint) + Ssz.AttestationDataLength + Ssz.BlsSignatureLength;

        public static int AttestationLength(Attestation? container)
        {
            if (container == null)
            {
                return 0;
            }

            return AttestationDynamicOffset + (container.AggregationBits.Length + 8) / 8;
        }

        public static int AttesterSlashingLength(AttesterSlashing? container)
        {
            if (container is null)
            {
                return 0;
            }
            
            return 2 * sizeof(uint) +
                   Ssz.IndexedAttestationLength(container.Attestation1) +
                   Ssz.IndexedAttestationLength(container.Attestation2);
        }

        public const int ContractTreeDepth = 32;
        public const int DepositLengthOfProof = (ContractTreeDepth + 1) * Ssz.Hash32Length;
        public const int DepositLength = DepositLengthOfProof + Ssz.DepositDataLength;
        public const int ProposerSlashingLength = Ssz.ValidatorIndexLength + 2 * Ssz.BeaconBlockHeaderLength;
        public const int VoluntaryExitLength = Ssz.EpochLength + Ssz.ValidatorIndexLength + Ssz.BlsSignatureLength;
        public const int BeaconBlockBodyDynamicOffset = Ssz.BlsSignatureLength + Ssz.Eth1DataLength + 32 + 5 * sizeof(uint);

        public static int BeaconBlockBodyLength(BeaconBlockBody? container)
        {
            if (container is null)
            {
                return 0;
            }

            int result = BeaconBlockBodyDynamicOffset;

            result += Ssz.ProposerSlashingLength * container.ProposerSlashings.Count;
            result += Ssz.DepositLength * container.Deposits.Count;
            result += Ssz.VoluntaryExitLength * container.VoluntaryExits.Count;

            result += sizeof(uint) * container.AttesterSlashings.Count;
            for (int i = 0; i < container.AttesterSlashings.Count; i++)
            {
                result += Ssz.AttesterSlashingLength(container.AttesterSlashings[i]);
            }

            result += sizeof(uint) * container.Attestations.Count;
            for (int i = 0; i < container.Attestations.Count; i++)
            {
                result += Ssz.AttestationLength(container.Attestations[i]);
            }

            return result;
        }

        public const int BeaconBlockDynamicOffset = Ssz.SlotLength + 2 * Ssz.Hash32Length + sizeof(uint) + Ssz.BlsSignatureLength;

        public static int BeaconBlockLength(BeaconBlock? container)
        {
            return container is null ? 0 : (BeaconBlockDynamicOffset + Ssz.BeaconBlockBodyLength(container.Body));
        }


        public static int BeaconStateDynamicOffset = sizeof(ulong) +
                                                     Ssz.SlotLength +
                                                     Ssz.ForkLength +
                                                     Ssz.BeaconBlockHeaderLength +
                                                     2 * SlotsPerHistoricalRoot * Ssz.Hash32Length +
                                                     sizeof(uint) +
                                                     Ssz.Eth1DataLength +
                                                     sizeof(uint) +
                                                     sizeof(ulong) +
                                                     2 * sizeof(uint) +
                                                     EpochsPerHistoricalVector * Ssz.Hash32Length +
                                                     EpochsPerSlashingsVector * Ssz.GweiLength +
                                                     2 * sizeof(uint) +
                                                     ((Ssz.JustificationBitsLength + 7) / 8) +
                                                     3 * Ssz.CheckpointLength;


        public static int BeaconStateLength(BeaconState? container)
        {
            if (container is null)
            {
                return 0;
            }
            
            int result = BeaconStateDynamicOffset;
            result += Ssz.Hash32Length * (container.HistoricalRoots?.Count ?? 0);
            result += Ssz.ValidatorLength * (container.Validators?.Count ?? 0);
            result += Ssz.GweiLength * (container.Balances?.Count ?? 0);
            result += Ssz.Eth1DataLength * (container.Eth1DataVotes?.Count ?? 0);

            result += (container.PreviousEpochAttestations?.Count ?? 0) * sizeof(uint);
            if (!(container.PreviousEpochAttestations is null))
            {
                for (int i = 0; i < container.PreviousEpochAttestations.Count; i++)
                {
                    result += Ssz.PendingAttestationLength(container.PreviousEpochAttestations[i]);
                }
            }

            result += (container.CurrentEpochAttestations?.Count ?? 0) * sizeof(uint);
            if (!(container.CurrentEpochAttestations is null))
            {
                for (int i = 0; i < container.CurrentEpochAttestations.Count; i++)
                {
                    result += Ssz.PendingAttestationLength(container.CurrentEpochAttestations[i]);
                }
            }

            return result;
        }
    }
}