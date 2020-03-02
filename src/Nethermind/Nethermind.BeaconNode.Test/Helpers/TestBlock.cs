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
        public static BeaconBlock BuildEmptyBlock(IServiceProvider testServiceProvider, BeaconState state, Slot slot)
        {
            //if (slot) is none

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            Eth1Data eth1Data = new Eth1Data(Root.Zero, state.Eth1DepositIndex, Bytes32.Zero);

            BeaconBlockHeader previousBlockHeader = BeaconBlockHeader.Clone(state.LatestBlockHeader);
            if (previousBlockHeader.StateRoot == Root.Zero)
            {
                Root stateRoot = cryptographyService.HashTreeRoot(state);
                previousBlockHeader.SetStateRoot(stateRoot);
            }
            Root previousBlockHashTreeRoot = previousBlockHeader.HashTreeRoot();

            BeaconBlock emptyBlock = new BeaconBlock(slot,
                previousBlockHashTreeRoot,
                Root.Zero,
                new BeaconBlockBody(
                    BlsSignature.Zero,
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

        public static BeaconBlock BuildEmptyBlockForNextSlot(IServiceProvider testServiceProvider, BeaconState state)
        {
            return BuildEmptyBlock(testServiceProvider, state, state.Slot + new Slot(1));
        }

        public static SignedBeaconBlock SignBlock(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block, ValidatorIndex proposerIndex)
        {
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            BeaconChainUtility beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            if (state.Slot > block.Slot)
            {
                throw new ArgumentOutOfRangeException("block.Slot", block.Slot, $"Slot of block must be equal or less that state slot {state.Slot}");
            }

            Epoch blockEpoch = beaconChainUtility.ComputeEpochAtSlot(block.Slot);
            if (proposerIndex == ValidatorIndex.None)
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

            Domain randaoDomain = beaconStateAccessor.GetDomain(state, signatureDomains.Randao, blockEpoch);
            Root epochRoot = cryptographyService.HashTreeRoot(blockEpoch);
            Root randaoRevealHash = beaconChainUtility.ComputeSigningRoot(epochRoot, randaoDomain);
            BlsSignature randaoReveal = TestSecurity.BlsSign(randaoRevealHash, privateKey);
            block.Body.SetRandaoReveal(randaoReveal);

            Root blockRoot = cryptographyService.HashTreeRoot(block);
            Domain signatureDomain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconProposer, blockEpoch);
            Root signingRoot = beaconChainUtility.ComputeSigningRoot(blockRoot, signatureDomain);
            BlsSignature signature = TestSecurity.BlsSign(signingRoot, privateKey);
            
            return new SignedBeaconBlock(block, signature);
        }
    }
}
