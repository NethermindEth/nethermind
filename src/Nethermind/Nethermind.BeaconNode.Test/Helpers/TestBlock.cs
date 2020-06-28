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
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestBlock
    {
        public static BeaconBlock BuildEmptyBlock(IServiceProvider testServiceProvider, BeaconState state, Slot slot, BlsSignature randaoReveal)
        {
            //if (slot) is none

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            Eth1Data eth1Data = new Eth1Data(Root.Zero, state.Eth1DepositIndex, Bytes32.Zero);

            Root stateRoot = !state.LatestBlockHeader.StateRoot.Equals(Root.Zero)
                ? state.LatestBlockHeader.StateRoot
                : cryptographyService.HashTreeRoot(state);
            BeaconBlockHeader previousBlockHeader = new BeaconBlockHeader(state.LatestBlockHeader.Slot,
                state.LatestBlockHeader.ParentRoot, stateRoot, state.LatestBlockHeader.BodyRoot);
            Root previousBlockHashTreeRoot = cryptographyService.HashTreeRoot(previousBlockHeader);

            if (randaoReveal.Equals(BlsSignature.Zero))
            {
                Epoch blockEpoch = beaconChainUtility.ComputeEpochAtSlot(slot);
                ValidatorIndex proposerIndex;
                if (slot == state.Slot)
                {
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
                }
                else
                {
                    Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
                    if (blockEpoch > stateEpoch + 1)
                    {
                        Console.WriteLine("WARNING: Block slot (epoch {0}) far away from state (epoch {1}), and no proposer index manually given."
                                          + " Signing block is slow due to transition for proposer index calculation.", blockEpoch, stateEpoch);
                    }

                    // use stub state to get proposer index of future slot
                    BeaconState stubState = BeaconState.Clone(state);
                    beaconStateTransition.ProcessSlots(stubState, slot);
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(stubState);
                }

                byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
                byte[] privateKey = privateKeys[(int) (ulong) proposerIndex];

                Domain randaoDomain = beaconStateAccessor.GetDomain(state, signatureDomains.Randao, blockEpoch);
                Root epochHashTreeRoot = cryptographyService.HashTreeRoot(blockEpoch);
                Root randaoSigningRoot = beaconChainUtility.ComputeSigningRoot(epochHashTreeRoot, randaoDomain);
                randaoReveal = TestSecurity.BlsSign(randaoSigningRoot, privateKey);
            }
            
            BeaconBlock emptyBlock = new BeaconBlock(slot,
                previousBlockHashTreeRoot,
                Root.Zero,
                new BeaconBlockBody(
                    randaoReveal,
                    eth1Data,
                    new Bytes32(),
                    Array.Empty<ProposerSlashing>(),
                    Array.Empty<AttesterSlashing>(),
                    Array.Empty<Attestation>(),
                    Array.Empty<Deposit>(),
                    Array.Empty<SignedVoluntaryExit>()
                ));

            return emptyBlock;
        }

        // randao = BlsSignature.Zero to generate randao
        public static BeaconBlock BuildEmptyBlockForNextSlot(IServiceProvider testServiceProvider, BeaconState state, BlsSignature randaoReveal)
        {
            return BuildEmptyBlock(testServiceProvider, state, state.Slot + new Slot(1), randaoReveal);
        }

        public static SignedBeaconBlock SignBlock(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block, ValidatorIndex? optionalProposerIndex)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            if (state.Slot > block.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Slot of block must be equal or less that state slot {state.Slot}");
            }

            Epoch blockEpoch = beaconChainUtility.ComputeEpochAtSlot(block.Slot);
            ValidatorIndex proposerIndex;
            if (optionalProposerIndex.HasValue)
            {
                proposerIndex = optionalProposerIndex.Value;
            }
            else
            {
                if (block.Slot == state.Slot)
                {
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
                }
                else
                {
                    Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
                    if (stateEpoch + 1 > blockEpoch)
                    {
                        Console.WriteLine("WARNING: Block slot far away, and no proposer index manually given."
                            + " Signing block is slow due to transition for proposer index calculation.");
                    }
                    // use stub state to get proposer index of future slot
                    BeaconState stubState = BeaconState.Clone(state);
                    beaconStateTransition.ProcessSlots(stubState, block.Slot);
                    proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(stubState);
                }
            }

            byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            byte[] privateKey = privateKeys[(int)(ulong)proposerIndex];

            Root blockHashTreeRoot = cryptographyService.HashTreeRoot(block);
            Domain proposerDomain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconProposer, blockEpoch);
            Root signingRoot = beaconChainUtility.ComputeSigningRoot(blockHashTreeRoot, proposerDomain);
            BlsSignature signature = TestSecurity.BlsSign(signingRoot, privateKey);
            
            return new SignedBeaconBlock(block, signature);
        }
    }
}
