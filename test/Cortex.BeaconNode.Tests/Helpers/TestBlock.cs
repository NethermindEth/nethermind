using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestBlock
    {
        public static BeaconBlock BuildEmptyBlock(BeaconState state, Slot slot, bool signed,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            //if (slot) is none

            var eth1Data = new Eth1Data(state.Eth1DepositIndex, Hash32.Zero);

            var previousBlockHeader = BeaconBlockHeader.Clone(state.LatestBlockHeader);
            if (previousBlockHeader.StateRoot == Hash32.Zero)
            {
                var stateRoot = state.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
                previousBlockHeader.SetStateRoot(stateRoot);
            }
            var previousBlockSigningRoot = previousBlockHeader.SigningRoot();

            var emptyBlock = new BeaconBlock(slot,
                previousBlockSigningRoot,
                Hash32.Zero,
                new BeaconBlockBody(
                    new BlsSignature(),
                    eth1Data,
                    new Bytes32(),
                    Array.Empty<ProposerSlashing>(),
                    Array.Empty<AttesterSlashing>(),
                    Array.Empty<Attestation>(),
                    Array.Empty<Deposit>()
                ),
                new BlsSignature());

            if (signed)
            {
                SignBlock(state, emptyBlock, ValidatorIndex.None,
                    miscellaneousParameters, timeParameters, maxOperationsPerBlock,
                    beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            }

            return emptyBlock;
        }

        public static BeaconBlock BuildEmptyBlockForNextSlot(BeaconState state, bool signed,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition)
        {
            return BuildEmptyBlock(state, state.Slot + new Slot(1), signed,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
        }

        public static void SignBlock(BeaconState state, BeaconBlock block, ValidatorIndex proposerIndex,
            MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor, BeaconStateTransition beaconStateTransition)
        {
            if (state.Slot > block.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Slot of block must be equal or less that state slot {state.Slot}");
            }

            var blockEpoch = beaconChainUtility.ComputeEpochAtSlot(block.Slot);
            if (proposerIndex == ValidatorIndex.None)
            {
                if (block.Slot == state.Slot)
                {
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
                }
                else
                {
                    var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
                    if (stateEpoch + new Epoch(1) > blockEpoch)
                    {
                        Console.WriteLine("WARNING: Block slot far away, and no proposer index manually given."
                            + " Signing block is slow due to transition for proposer index calculation.");
                    }
                    // use stub state to get proposer index of future slot
                    var stubState = BeaconState.Clone(state);
                    beaconStateTransition.ProcessSlots(stubState, block.Slot);
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(stubState);
                }
            }

            var privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            var privateKey = privateKeys[(int)(ulong)proposerIndex];

            var domain = beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, blockEpoch);

            var randaoRevealHash = blockEpoch.HashTreeRoot();
            var randaoReveal = TestUtility.BlsSign(randaoRevealHash, privateKey, domain);
            block.Body.SetRandaoReveal(randaoReveal);

            var signingRoot = block.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var signature = TestUtility.BlsSign(signingRoot, privateKey, domain);
            block.SetSignature(signature);
        }
    }
}
