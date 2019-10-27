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
        public static void AddAttestationToState(BeaconState state, Attestation attestation, Slot slot,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            var block = TestBlock.BuildEmptyBlockForNextSlot(state, false, miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
            block.SetSlot(slot);
            block.Body.AddAttestations(attestation);
            beaconStateTransition.ProcessSlots(state, block.Slot);
            TestBlock.SignBlock(state, block, ValidatorIndex.None, timeParameters, maxOperationsPerBlock, beaconChainUtility, beaconStateAccessor);
            beaconStateTransition.StateTransition(state, block);
        }

        public static BlsSignature GetAttestationSignature(BeaconState state, AttestationData attestationData, byte[] privateKey, bool custodyBit,
            BeaconStateAccessor beaconStateAccessor)
        {
            var message = new AttestationDataAndCustodyBit(attestationData, custodyBit);
            var messageHash = message.HashTreeRoot();
            var domain = beaconStateAccessor.GetDomain(state, DomainType.BeaconAttester, attestationData.Target.Epoch);
            var signature = TestUtility.BlsSign(messageHash, privateKey, domain);
            return signature;
        }

        public static Attestation GetValidAttestation(BeaconState state, Slot slot, bool signed,
                    MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            var epoch = beaconChainUtility.ComputeEpochOfSlot(slot);
            var epochStartShard = beaconStateAccessor.GetStartShard(state, epoch);
            var committeesPerSlot = beaconStateAccessor.GetCommitteeCount(state, epoch) / (ulong)timeParameters.SlotsPerEpoch;
            var shard = (epochStartShard + new Shard(committeesPerSlot * (ulong)(slot % timeParameters.SlotsPerEpoch)))
                % miscellaneousParameters.ShardCount;

            var attestationData = BuildAttestationData(state, slot, shard,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor);

            var crosslinkCommittee = beaconStateAccessor.GetCrosslinkCommittee(state, attestationData.Target.Epoch, attestationData.Crosslink.Shard);

            var committeeSize = crosslinkCommittee.Count;
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

            return TestUtility.BlsAggregateSignatures(signatures);
\        }

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

        private static AttestationData BuildAttestationData(BeaconState state, Slot slot, Shard shard,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor)
        {
            if (state.Slot > slot)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Slot cannot be greater than state slot {state.Slot}.");
            }

            Hash32 blockRoot;
            if (slot == state.Slot)
            {
                var nextBlock = TestBlock.BuildEmptyBlockForNextSlot(state, false,
                    miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
                blockRoot = nextBlock.ParentRoot;
            }
            else
            {
                throw new NotImplementedException();
            }

            Hash32 epochBoundaryRoot;
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var currentEpochStartSlot = beaconChainUtility.ComputeStartSlotOfEpoch(currentEpoch);
            if (slot < currentEpochStartSlot)
            {
                throw new NotImplementedException();
            }
            else if (slot == currentEpochStartSlot)
            {
                epochBoundaryRoot = blockRoot;
            }
            else
            {
                throw new NotImplementedException();
            }

            Epoch sourceEpoch;
            Hash32 sourceRoot;
            if (slot < currentEpochStartSlot)
            {
                throw new NotImplementedException();
            }
            else
            {
                sourceEpoch = state.CurrentJustifiedCheckpoint.Epoch;
                sourceRoot = state.CurrentJustifiedCheckpoint.Root;
            }

            Crosslink parentCrosslink;
            var epochOfSlot = beaconChainUtility.ComputeEpochOfSlot(slot);
            if (epochOfSlot == currentEpoch)
            {
                parentCrosslink = state.CurrentCrosslinks[(int)(ulong)shard];
            }
            else
            {
                throw new NotImplementedException();
            }

            var attestationData = new AttestationData(
                blockRoot,
                new Checkpoint(sourceEpoch, sourceRoot),
                new Checkpoint(epochOfSlot, epochBoundaryRoot),
                new Crosslink(
                    shard,
                    parentCrosslink.HashTreeRoot(),
                    parentCrosslink.EndEpoch,
                    Epoch.Min(epochOfSlot, parentCrosslink.EndEpoch + timeParameters.MaximumEpochsPerCrosslink),
                    Hash32.Zero
                )
            );

            return attestationData;
        }

        private static void FillAggregateAttestation(BeaconState state, Attestation attestation,
                    BeaconStateAccessor beaconStateAccessor)
        {
            var crosslinkCommittee = beaconStateAccessor.GetCrosslinkCommittee(state,
                attestation.Data.Target.Epoch,
                attestation.Data.Crosslink.Shard);
            for (var i = 0; i < crosslinkCommittee.Count; i++)
            {
                attestation.AggregationBits[i] = true;
            }
        }
    }
}
