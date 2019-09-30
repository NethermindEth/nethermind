using System.Collections.Generic;
using System.Threading.Tasks;
using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        public BeaconChain()
        {
            // FIXME: This is a testing intialization
            State = new BeaconState(106185600);
        }

        public BeaconState State { get; }

        public async Task<bool> TryGenesisAsync(byte[] eth1BlockHash, ulong eth1Timestamp, IList<Deposit> deposits)
        {
            // genesis
            // candidate_state = initialize_beacon_state_from_eth1(eth1_block_hash, eth1_timestamp, deposits)
            // if is_valid_genesis_state(candidate_state) then genesis_state = candidate_state
            // store = get_genesis_store(genesis_state)
            //         genesis_block = BeaconBlock(state_root=hash_tree_root(genesis_state))
            return false;
        }


        // Update store via... (store processor ?)

        // on_tick

        // on_block(store, block)
        //          state_transition(pre_state, block)

        // on_attestation
    }
}
