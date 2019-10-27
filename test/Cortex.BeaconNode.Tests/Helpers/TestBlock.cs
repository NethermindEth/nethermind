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
            MaxOperationsPerBlock maxOperationsPerBlock)
        {
            //if (slot) is none

            var eth1Data = new Eth1Data(Hash32.Zero, state.Eth1DepositIndex);

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
                new BeaconBlockBody(new BlsSignature(), eth1Data, new Bytes32(), Array.Empty<Deposit>()),
                new BlsSignature());

            if (signed)
            {
                throw new NotImplementedException();
                //SignBlock(state, emptyBlock);
            }

            return emptyBlock;
        }

        public static BeaconBlock BuildEmptyBlockForNextSlot(BeaconState state, bool signed,
            MiscellaneousParameters miscellaneousParameters,
            TimeParameters timeParameters,
            StateListLengths stateListLengths,
            MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return BuildEmptyBlock(state, state.Slot + new Slot(1), signed,
                miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
        }

        public static void SignBlock(BeaconState state, BeaconBlock block, ValidatorIndex proposerIndex,
            TimeParameters timeParameters, MaxOperationsPerBlock maxOperationsPerBlock,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            if (state.Slot > block.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Slot of block must be equal or less that state slot {state.Slot}");
            }

            if (proposerIndex == ValidatorIndex.None)
            {
                if (block.Slot == state.Slot)
                {
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
                }
                else
                {
                    throw new NotImplementedException();
                }
            }

            var privateKeys = TestData.PrivateKeys(timeParameters).ToArray();
            var privateKey = privateKeys[(int)(ulong)proposerIndex];

            var blockEpoch = beaconChainUtility.ComputeEpochOfSlot(block.Slot);
            var domain = beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, blockEpoch);

            var randaoRevealHash = blockEpoch.HashTreeRoot();
            var randaoReveal = TestUtility.BlsSign(randaoRevealHash, privateKey, domain);
            block.Body.SetRandaoReveal(randaoReveal);

            var signingRoot = block.SigningRoot(maxOperationsPerBlock);
            var signature = TestUtility.BlsSign(signingRoot, privateKey, domain);
            block.SetSignature(signature);
        }
    }
}
