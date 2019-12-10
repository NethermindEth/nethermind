using System;
using System.Threading.Tasks;
using Nethermind.BeaconNode.Storage;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Attestation = Nethermind.BeaconNode.Containers.Attestation;
using AttesterSlashing = Nethermind.BeaconNode.Containers.AttesterSlashing;
using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.Containers.BeaconBlockBody;
using BeaconState = Nethermind.BeaconNode.Containers.BeaconState;
using Deposit = Nethermind.BeaconNode.Containers.Deposit;
using Eth1Data = Nethermind.BeaconNode.Containers.Eth1Data;
using Hash32 = Nethermind.Core2.Types.Hash32;
using ProposerSlashing = Nethermind.BeaconNode.Containers.ProposerSlashing;

namespace Nethermind.BeaconNode
{
    public class BlockProducer
    {
        private readonly ForkChoice _forkChoice;
        private readonly IStoreProvider _storeProvider;

        public BlockProducer(ForkChoice forkChoice, IStoreProvider storeProvider)
        {
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
        }

        public async Task<BeaconState> GetHeadStateAsync()
        {
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;
            Hash32 head = await _forkChoice.GetHeadAsync(store);
            if (!store.TryGetBlockState(head, out BeaconState? state))
            {
                throw new Exception($"Beacon chain is currently syncing, head state {head} not found.");
            }

            return state!;
        }

        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal)
        {
            if (!_storeProvider.TryGetStore(out IStore? store))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            Hash32 head = await _forkChoice.GetHeadAsync(store!);

            // get previous head block, based on the fork choice
            // get latest state
            // process outstanding slots for state
            // get parent header (state root, signature, slot, parent root, body root)

            BeaconBlockBody body = new BeaconBlockBody(randaoReveal,
                new Eth1Data(0, Hash32.Zero), 
                new Bytes32(), 
                Array.Empty<ProposerSlashing>(),
                Array.Empty<AttesterSlashing>(),
                Array.Empty<Attestation>(),
                Array.Empty<Deposit>(),
                Array.Empty<VoluntaryExit>());
            BeaconBlock block = new BeaconBlock(slot, Hash32.Zero, Hash32.Zero, body, BlsSignature.Empty);

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
