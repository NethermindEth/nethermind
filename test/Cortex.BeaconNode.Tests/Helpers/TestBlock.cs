using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestBlock
    {
        public static BeaconBlock BuildEmptyBlock(IServiceProvider testServiceProvider, BeaconState state, Slot slot, bool signed)
        {
            //if (slot) is none

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

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
                    Array.Empty<Deposit>(),
                    Array.Empty<VoluntaryExit>()
                ),
                new BlsSignature());

            if (signed)
            {
                SignBlock(testServiceProvider, state, emptyBlock, ValidatorIndex.None);
            }

            return emptyBlock;
        }

        public static BeaconBlock BuildEmptyBlockForNextSlot(IServiceProvider testServiceProvider, BeaconState state, bool signed)
        {
            return BuildEmptyBlock(testServiceProvider, state, state.Slot + new Slot(1), signed);
        }

        public static void SignBlock(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block, ValidatorIndex proposerIndex)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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

            var randaoDomain = beaconStateAccessor.GetDomain(state, DomainType.Randao, blockEpoch);
            var randaoRevealHash = blockEpoch.HashTreeRoot();
            var randaoReveal = TestSecurity.BlsSign(randaoRevealHash, privateKey, randaoDomain);
            block.Body.SetRandaoReveal(randaoReveal);

            var signatureDomain = beaconStateAccessor.GetDomain(state, DomainType.BeaconProposer, blockEpoch);
            var signingRoot = block.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var signature = TestSecurity.BlsSign(signingRoot, privateKey, signatureDomain);
            block.SetSignature(signature);
        }
    }
}
