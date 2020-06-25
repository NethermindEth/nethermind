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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestAttestation
    {
        public static void AddAttestationsToState(IServiceProvider testServiceProvider, BeaconState state, IEnumerable<Attestation> attestations, Slot slot)
        {
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            BeaconBlock block = TestBlock.BuildEmptyBlock(testServiceProvider, state, slot, BlsSignature.Zero);
            foreach (Attestation attestation in attestations)
            {
                block.Body.AddAttestations(attestation);
            }
            beaconStateTransition.ProcessSlots(state, block.Slot);
            
            SignedBeaconBlock signedBlock = TestBlock.SignBlock(testServiceProvider, state, block, ValidatorIndex.None);
            beaconStateTransition.StateTransition(state, signedBlock, validateResult: false);
        }

        public static BlsSignature GetAttestationSignature(IServiceProvider testServiceProvider, BeaconState state, AttestationData attestationData, byte[] privateKey)
        {
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            Root attestationDataRoot = attestationData.HashTreeRoot();
            Domain domain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconAttester, attestationData.Target.Epoch);
            Root signingRoot = beaconChainUtility.ComputeSigningRoot(attestationDataRoot, domain);
            BlsSignature signature = TestSecurity.BlsSign(signingRoot, privateKey);
            return signature;
        }

        // def get_valid_attestation(spec, state, slot=None, index=None, signed=False):
        public static Attestation GetValidAttestation(IServiceProvider testServiceProvider, BeaconState state, Slot? optionalSlot, CommitteeIndex? optionalIndex, bool signed)
        {
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            Slot slot = optionalSlot ?? state.Slot;
            CommitteeIndex index = optionalIndex ?? CommitteeIndex.Zero;

            AttestationData attestationData = BuildAttestationData(testServiceProvider, state, slot, index);

            IReadOnlyList<ValidatorIndex> beaconCommittee = beaconStateAccessor.GetBeaconCommittee(state, attestationData.Slot, attestationData.Index);

            int committeeSize = beaconCommittee.Count;
            BitArray aggregationBits = new BitArray(committeeSize);
            Attestation attestation = new Attestation(aggregationBits, attestationData, BlsSignature.Zero);

            FillAggregateAttestation(state, attestation, beaconStateAccessor);

            if (signed)
            {
                SignAttestation(testServiceProvider, state, attestation);
            }

            return attestation;
        }

        public static BlsSignature SignAggregateAttestation(IServiceProvider testServiceProvider, BeaconState state, AttestationData attestationData, IEnumerable<ValidatorIndex> participants)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            List<byte[]> privateKeys = TestKeys.PrivateKeys(timeParameters).ToList();
            List<BlsSignature> signatures = new List<BlsSignature>();
            foreach (ValidatorIndex validatorIndex in participants)
            {
                byte[] privateKey = privateKeys[(int)(ulong)validatorIndex];
                BlsSignature signature = GetAttestationSignature(testServiceProvider, state, attestationData, privateKey);
                signatures.Add(signature);
            }

            return TestSecurity.BlsAggregateSignatures(signatures);
        }

        public static void SignAttestation(IServiceProvider testServiceProvider, BeaconState state, Attestation attestation)
        {
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            IReadOnlyList<ValidatorIndex> participants = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            BlsSignature signature = SignAggregateAttestation(testServiceProvider, state, attestation.Data, participants);
            attestation.SetSignature(signature);
        }

        private static AttestationData BuildAttestationData(IServiceProvider testServiceProvider, BeaconState state, Slot slot, CommitteeIndex index)
        {
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            if (state.Slot > slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot cannot be greater than state slot {state.Slot}.");
            }

            Root blockRoot;
            if (slot == state.Slot)
            {
                BeaconBlock nextBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
                blockRoot = nextBlock.ParentRoot;
            }
            else
            {
                blockRoot = beaconStateAccessor.GetBlockRootAtSlot(state, slot);
            }

            Root epochBoundaryRoot;
            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            Slot currentEpochStartSlot = beaconChainUtility.ComputeStartSlotOfEpoch(currentEpoch);
            if (slot < currentEpochStartSlot)
            {
                Epoch previousEpoch = beaconStateAccessor.GetPreviousEpoch(state);
                epochBoundaryRoot = beaconStateAccessor.GetBlockRoot(state, previousEpoch);
            }
            else if (slot == currentEpochStartSlot)
            {
                epochBoundaryRoot = blockRoot;
            }
            else
            {
                epochBoundaryRoot = beaconStateAccessor.GetBlockRoot(state, currentEpoch);
            }

            Epoch sourceEpoch;
            Root sourceRoot;
            if (slot < currentEpochStartSlot)
            {
                sourceEpoch = state.PreviousJustifiedCheckpoint.Epoch;
                sourceRoot = state.PreviousJustifiedCheckpoint.Root;
            }
            else
            {
                sourceEpoch = state.CurrentJustifiedCheckpoint.Epoch;
                sourceRoot = state.CurrentJustifiedCheckpoint.Root;
            }

            //Crosslink parentCrosslink;
            //if (epochOfSlot == currentEpoch)
            //{
            //    parentCrosslink = state.CurrentCrosslinks[(int)(ulong)shard];
            //}
            //else
            //{
            //    throw new NotImplementedException();
            //}

            Epoch slotEpoch = beaconChainUtility.ComputeEpochAtSlot(slot);
            AttestationData attestationData = new AttestationData(
                slot,
                index,
                blockRoot,
                new Checkpoint(sourceEpoch, sourceRoot),
                new Checkpoint(slotEpoch, epochBoundaryRoot));

            return attestationData;
        }

        private static void FillAggregateAttestation(BeaconState state, Attestation attestation,
                    BeaconStateAccessor beaconStateAccessor)
        {
            IReadOnlyList<ValidatorIndex> beaconCommittee = beaconStateAccessor.GetBeaconCommittee(state, attestation.Data.Slot, attestation.Data.Index);

            for (int i = 0; i < beaconCommittee.Count; i++)
            {
                attestation.AggregationBits[i] = true;
            }
        }
    }
}
