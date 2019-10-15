
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconStateExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconState item, StateListLengths stateListLengths)
        {
            var tree = new SszTree(item.ToSszContainer(stateListLengths));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconState item, StateListLengths stateListLengths)
        {
            return new SszContainer(GetValues(item, stateListLengths));
        }

        private static IEnumerable<SszElement> GetValues(BeaconState item, StateListLengths stateListLengths)
        {
            //# Versioning
            //genesis_time: uint64
            yield return item.GenesisTime.ToSszBasicElement();
            //slot: Slot
            //fork: Fork

            //# History
            //latest_block_header: BeaconBlockHeader
            yield return item.LatestBlockHeader.ToSszContainer();
            //block_roots: Vector[Hash, SLOTS_PER_HISTORICAL_ROOT]
            //state_roots: Vector[Hash, SLOTS_PER_HISTORICAL_ROOT]
            //historical_roots: List[Hash, HISTORICAL_ROOTS_LIMIT]

            //# Eth1
            //eth1_data: Eth1Data
            yield return item.Eth1Data.ToSszContainer();
            //eth1_data_votes: List[Eth1Data, SLOTS_PER_ETH1_VOTING_PERIOD]
            //eth1_deposit_index: uint64
            yield return item.Eth1DepositIndex.ToSszBasicElement();

            //# Registry
            //validators: List[Validator, VALIDATOR_REGISTRY_LIMIT]
            yield return new SszList(item.Validators.Select(x => x.ToSszContainer()), stateListLengths.ValidatorRegistryLimit);
            //balances: List[Gwei, VALIDATOR_REGISTRY_LIMIT]
            yield return new SszBasicList(item.Balances.Cast<ulong>().ToArray(), stateListLengths.ValidatorRegistryLimit);

            //# Shuffling
            //start_shard: Shard
            //randao_mixes: Vector[Hash, EPOCHS_PER_HISTORICAL_VECTOR]

            //# Slashings
            //slashings: Vector[Gwei, EPOCHS_PER_SLASHINGS_VECTOR]  # Per-epoch sums of slashed effective balances

            //# Attestations
            //previous_epoch_attestations: List[PendingAttestation, MAX_ATTESTATIONS * SLOTS_PER_EPOCH]
            //current_epoch_attestations: List[PendingAttestation, MAX_ATTESTATIONS * SLOTS_PER_EPOCH]

            //# Crosslinks
            //previous_crosslinks: Vector[Crosslink, SHARD_COUNT]  # Previous epoch snapshot
            //current_crosslinks: Vector[Crosslink, SHARD_COUNT]

            //# Finality
            //justification_bits: Bitvector[JUSTIFICATION_BITS_LENGTH]  # Bit set for every recent justified epoch
            //previous_justified_checkpoint: Checkpoint  # Previous epoch snapshot
            //current_justified_checkpoint: Checkpoint
            //finalized_checkpoint: Checkpoint
        }
    }
}
