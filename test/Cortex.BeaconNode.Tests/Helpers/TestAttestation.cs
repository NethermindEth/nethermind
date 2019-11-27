using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestAttestation
    {
        public static void AddAttestationsToState(BeaconState state, IEnumerable<Attestation> attestations, Slot slot,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            var block = TestBlock.BuildEmptyBlockForNextSlot(state, false,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            block.SetSlot(slot);
            foreach (var attestation in attestations)
            {
                block.Body.AddAttestations(attestation);
            }
            beaconStateTransition.ProcessSlots(state, block.Slot);
            TestBlock.SignBlock(state, block, ValidatorIndex.None,
                miscellaneousParameters, timeParameters, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            beaconStateTransition.StateTransition(state, block, validateStateRoot: false);
        }

        public static BlsSignature GetAttestationSignature(BeaconState state, AttestationData attestationData, byte[] privateKey, bool custodyBit,
            BeaconStateAccessor beaconStateAccessor)
        {
            var message = new AttestationDataAndCustodyBit(attestationData, custodyBit);
            var messageHash = message.HashTreeRoot();
            var domain = beaconStateAccessor.GetDomain(state, DomainType.BeaconAttester, attestationData.Target.Epoch);
            var signature = TestSecurity.BlsSign(messageHash, privateKey, domain);
            return signature;
        }

        // def get_valid_attestation(spec, state, slot=None, index=None, signed=False):
        public static Attestation GetValidAttestation(BeaconState state, Slot slot, CommitteeIndex index, bool signed,
                    MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            if (slot == Slot.None)
            {
                slot = state.Slot;
            }
            if (index == CommitteeIndex.None)
            {
                index = new CommitteeIndex(0);
            }

            var attestationData = BuildAttestationData(state, slot, index,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            var beaconCommittee = beaconStateAccessor.GetBeaconCommittee(state, attestationData.Slot, attestationData.Index);

            var committeeSize = beaconCommittee.Count;
            var aggregationBits = new BitArray(committeeSize);
            var custodyBits = new BitArray(committeeSize);
            var attestation = new Attestation(aggregationBits, attestationData, custodyBits, new BlsSignature());

            FillAggregateAttestation(state, attestation, beaconStateAccessor);

            if (signed)
            {
                SignAttestation(state, attestation, timeParameters, beaconStateAccessor);
            }

            return attestation;
        }

        public static BlsSignature SignAggregateAttestation(BeaconState state, AttestationData attestationData, IEnumerable<ValidatorIndex> participants,
            TimeParameters timeParameters,
            BeaconStateAccessor beaconStateAccessor)
        {
            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToList();
            var signatures = new List<BlsSignature>();
            foreach (var validatorIndex in participants)
            {
                var privateKey = privateKeys[(int)(ulong)validatorIndex];
                var signature = GetAttestationSignature(state, attestationData, privateKey, custodyBit: false,
                    beaconStateAccessor);
                signatures.Add(signature);
            }

            return TestSecurity.BlsAggregateSignatures(signatures);
        }

        public static void SignAttestation(BeaconState state, Attestation attestation,
                    TimeParameters timeParameters,
            BeaconStateAccessor beaconStateAccessor)
        {
            var participants = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            var signature = SignAggregateAttestation(state, attestation.Data, participants,
                timeParameters,
                beaconStateAccessor);
            attestation.SetSignature(signature);
        }

        private static AttestationData BuildAttestationData(BeaconState state, Slot slot, CommitteeIndex index,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            if (state.Slot > slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot cannot be greater than state slot {state.Slot}.");
            }

            Hash32 blockRoot;
            if (slot == state.Slot)
            {
                var nextBlock = TestBlock.BuildEmptyBlockForNextSlot(state, false,
                    miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                    beaconChainUtility, beaconStateAccessor, beaconStateTransition);
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
