using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconStateExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconState item, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, StateListLengths stateListLengths, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconState item, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, StateListLengths stateListLengths, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock));
        }

        private static IEnumerable<SszElement> GetValues(BeaconState item, MiscellaneousParameters miscellaneousParameters, TimeParameters timeParameters, StateListLengths stateListLengths, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            //# Versioning
            yield return item.GenesisTime.ToSszBasicElement();
            yield return item.Slot.ToSszBasicElement();
            yield return item.Fork.ToSszContainer();

            //# History
            yield return item.LatestBlockHeader.ToSszContainer();
            yield return item.BlockRoots.ToSszVector();
            yield return item.StateRoots.ToSszVector();
            //historical_roots: List[Hash, HISTORICAL_ROOTS_LIMIT]

            //# Eth1
            yield return item.Eth1Data.ToSszContainer();
            //eth1_data_votes: List[Eth1Data, SLOTS_PER_ETH1_VOTING_PERIOD]
            yield return item.Eth1DepositIndex.ToSszBasicElement();

            //# Registry
            yield return new SszList(item.Validators.Select(x => x.ToSszContainer()), stateListLengths.ValidatorRegistryLimit);
            yield return item.Balances.ToSszBasicList(stateListLengths.ValidatorRegistryLimit);

            //# Randomness
            //yield return item.StartShard.ToSszBasicElement();
            yield return item.RandaoMixes.ToSszVector();

            //# Slashings
            //slashings: Vector[Gwei, EPOCHS_PER_SLASHINGS_VECTOR]  # Per-epoch sums of slashed effective balances

            //# Attestations
            yield return item.PreviousEpochAttestations.ToSszList(maxOperationsPerBlock.MaximumAttestations * (ulong)timeParameters.SlotsPerEpoch, miscellaneousParameters);
            yield return item.CurrentEpochAttestations.ToSszList(maxOperationsPerBlock.MaximumAttestations * (ulong)timeParameters.SlotsPerEpoch, miscellaneousParameters);

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
