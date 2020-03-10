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

using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class BeaconStateExtensions
    {
        public static Root HashTreeRoot(this BeaconState item, ulong historicalRootsLimit, ulong slotsPerEth1VotingPeriod, ulong validatorRegistryLimit, ulong maximumAttestationsPerEpoch, ulong maximumValidatorsPerCommittee)
        {
            var tree = new SszTree(item.ToSszContainer(historicalRootsLimit, slotsPerEth1VotingPeriod, validatorRegistryLimit, maximumAttestationsPerEpoch, maximumValidatorsPerCommittee));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconState item, ulong historicalRootsLimit, ulong slotsPerEth1VotingPeriod, ulong validatorRegistryLimit, ulong maximumAttestationsPerEpoch, ulong maximumValidatorsPerCommittee)
        {
            return new SszContainer(GetValues(item, historicalRootsLimit, slotsPerEth1VotingPeriod, validatorRegistryLimit, maximumAttestationsPerEpoch, maximumValidatorsPerCommittee));
        }

        private static IEnumerable<SszElement> GetValues(BeaconState item, ulong historicalRootsLimit, ulong slotsPerEth1VotingPeriod, ulong validatorRegistryLimit, ulong maximumAttestationsPerEpoch, ulong maximumValidatorsPerCommittee)
        {
            //# Versioning
            yield return item.GenesisTime.ToSszBasicElement();
            yield return item.Slot.ToSszBasicElement();
            yield return item.Fork.ToSszContainer();

            //# History
            yield return item.LatestBlockHeader.ToSszContainer();
            yield return item.BlockRoots.ToSszVector();
            yield return item.StateRoots.ToSszVector();
            yield return item.HistoricalRoots.ToSszList(historicalRootsLimit);

            //# Eth1
            yield return item.Eth1Data.ToSszContainer();
            yield return new SszList(item.Eth1DataVotes.Select(x => Eth1DataExtensions.ToSszContainer(x)), slotsPerEth1VotingPeriod);
            yield return item.Eth1DepositIndex.ToSszBasicElement();

            //# Registry
            yield return new SszList(item.Validators.Select(x => ValidatorExtensions.ToSszContainer(x)), validatorRegistryLimit);
            yield return item.Balances.ToSszBasicList(validatorRegistryLimit);

            //# Randomness
            //yield return item.StartShard.ToSszBasicElement();
            yield return item.RandaoMixes.ToSszVector();

            //# Slashings
            //Per-epoch sums of slashed effective balances
            yield return item.Slashings.ToSszBasicVector();

            //# Attestations
            yield return item.PreviousEpochAttestations.ToSszList(maximumAttestationsPerEpoch, maximumValidatorsPerCommittee);
            yield return item.CurrentEpochAttestations.ToSszList(maximumAttestationsPerEpoch, maximumValidatorsPerCommittee);

            //# Crosslinks
            //# Previous epoch snapshot
            //yield return item.PreviousCrosslinks.ToSszVector();
            //yield return item.CurrentCrosslinks.ToSszVector();

            //# Finality
            // Bit set for every recent justified epoch
            yield return item.JustificationBits.ToSszBitvector();
            yield return item.PreviousJustifiedCheckpoint.ToSszContainer();
            yield return item.CurrentJustifiedCheckpoint.ToSszContainer();
            yield return item.FinalizedCheckpoint.ToSszContainer();
        }
    }
}
