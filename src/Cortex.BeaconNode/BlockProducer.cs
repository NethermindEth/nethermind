using System;
using System.Threading.Tasks;
using Cortex.BeaconNode.Data;
using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public class BlockProducer
    {
        private readonly IStoreProvider _storeProvider;

        public BlockProducer(IStoreProvider storeProvider)
        {
            _storeProvider = storeProvider;
        }

        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal)
        {
            var store = _storeProvider.GetStore();

            var head = await store.GetHeadAsync();

            // get previous head block, based on the fork choice
            // get latest state
            // process outstanding slots for state
            // get parent header (state root, signature, slot, parent root, body root)

            var body = new BeaconBlockBody(randaoReveal,
                new Eth1Data(0, Hash32.Zero), 
                new Bytes32(), 
                Array.Empty<ProposerSlashing>(),
                Array.Empty<AttesterSlashing>(),
                Array.Empty<Attestation>(),
                Array.Empty<Deposit>(),
                Array.Empty<VoluntaryExit>());
            var block = new BeaconBlock(slot, Hash32.Zero, Hash32.Zero, body, new BlsSignature());

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
