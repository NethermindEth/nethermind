// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
