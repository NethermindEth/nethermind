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
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestAttestation
    {
        public static void AddAttestationsToState(IServiceProvider testServiceProvider, BeaconState state, IEnumerable<Attestation> attestations, Slot slot)
        {
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, false);
            block.SetSlot(slot);
            foreach (var attestation in attestations)
            {
                block.Body.AddAttestations(attestation);
            }
            beaconStateTransition.ProcessSlots(state, block.Slot);
            TestBlock.SignBlock(testServiceProvider, state, block, ValidatorIndex.None);
            beaconStateTransition.StateTransition(state, block, validateStateRoot: false);
        }

        public static BlsSignature GetAttestationSignature(IServiceProvider testServiceProvider, BeaconState state, AttestationData attestationData, byte[] privateKey, bool custodyBit)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var message = new AttestationDataAndCustodyBit(attestationData, custodyBit);
            var messageHash = message.HashTreeRoot();
            var domain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconAttester, attestationData.Target.Epoch);
            var signature = TestSecurity.BlsSign(messageHash, privateKey, domain);
            return signature;
        }

        // def get_valid_attestation(spec, state, slot=None, index=None, signed=False):
        public static Attestation GetValidAttestation(IServiceProvider testServiceProvider, BeaconState state, Slot slot, CommitteeIndex index, bool signed)
        {
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            if (slot == Slot.None)
            {
                slot = state.Slot;
            }
            if (index == CommitteeIndex.None)
            {
                index = new CommitteeIndex(0);
            }

            var attestationData = BuildAttestationData(testServiceProvider, state, slot, index);

            var beaconCommittee = beaconStateAccessor.GetBeaconCommittee(state, attestationData.Slot, attestationData.Index);

            var committeeSize = beaconCommittee.Count;
            var aggregationBits = new BitArray(committeeSize);
            var custodyBits = new BitArray(committeeSize);
            var attestation = new Attestation(aggregationBits, attestationData, custodyBits, BlsSignature.Empty);

            FillAggregateAttestation(state, attestation, beaconStateAccessor);

            if (signed)
            {
                SignAttestation(testServiceProvider, state, attestation);
            }

            return attestation;
        }

        public static BlsSignature SignAggregateAttestation(IServiceProvider testServiceProvider, BeaconState state, AttestationData attestationData, IEnumerable<ValidatorIndex> participants)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToList();
            var signatures = new List<BlsSignature>();
            foreach (var validatorIndex in participants)
            {
                var privateKey = privateKeys[(int)(ulong)validatorIndex];
                var signature = GetAttestationSignature(testServiceProvider, state, attestationData, privateKey, custodyBit: false);
                signatures.Add(signature);
            }

            return TestSecurity.BlsAggregateSignatures(signatures);
        }

        public static void SignAttestation(IServiceProvider testServiceProvider, BeaconState state, Attestation attestation)
        {
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var participants = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            var signature = SignAggregateAttestation(testServiceProvider, state, attestation.Data, participants);
            attestation.SetSignature(signature);
        }

        private static AttestationData BuildAttestationData(IServiceProvider testServiceProvider, BeaconState state, Slot slot, CommitteeIndex index)
        {
            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            if (state.Slot > slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot cannot be greater than state slot {state.Slot}.");
            }

            Hash32 blockRoot;
            if (slot == state.Slot)
            {
                var nextBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, false);
                blockRoot = nextBlock.ParentRoot;
            }
            else
            {
                blockRoot = beaconStateAccessor.GetBlockRootAtSlot(state, slot);
            }

            Hash32 epochBoundaryRoot;
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var currentEpochStartSlot = beaconChainUtility.ComputeStartSlotOfEpoch(currentEpoch);
            if (slot < currentEpochStartSlot)
            {
                var previousEpoch = beaconStateAccessor.GetPreviousEpoch(state);
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
            Hash32 sourceRoot;
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

            var slotEpoch = beaconChainUtility.ComputeEpochAtSlot(slot);
            var attestationData = new AttestationData(
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
            var beaconCommittee = beaconStateAccessor.GetBeaconCommittee(state, attestation.Data.Slot, attestation.Data.Index);

            for (var i = 0; i < beaconCommittee.Count; i++)
            {
                attestation.AggregationBits[i] = true;
            }
        }
    }
}
