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
