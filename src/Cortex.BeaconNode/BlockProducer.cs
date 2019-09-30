using System;
using System.Threading.Tasks;
using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public class BlockProducer
    {
        public BlockProducer()
        {
        }

        public async Task<BeaconBlock> NewBlockAsync(ulong slot, byte[] randaoReveal)
        {
            // get previous head block, based on the fork choice
            // get latest state
            // process outstanding slots for state
            // get parent header (state root, signature, slot, parent root, body root)

            var block = new BeaconBlock(slot, randaoReveal);

            // new block = slot, parent root,
            //  signature = null
            //  body = assemble body

            // assemble body:
            //  from opPool get proposer slashings, attester slashings, attestations, voluntary exits
            //  get eth1data
            // generate deposits (based on new data)
            //     -> if eth1data deposit count > state deposit index, then get from op pool, sort and calculate merkle tree
            // deposit root = merkleRoot
            // randaoReveal

            // apply block to state transition
            // block.stateRoot = new state hash

            return await Task.Run(() => block);
        }
    }
}
