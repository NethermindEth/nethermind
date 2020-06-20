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

using System.Threading.Tasks;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    public class StoreAccessor
    {
        public async Task<Root> GetAncestorAsync(IStore store, Root root, Slot slot)
        {
            SignedBeaconBlock signedBlock = await store.GetSignedBlockAsync(root).ConfigureAwait(false);

            if (signedBlock.Message.Slot > slot)
            {
                return await GetAncestorAsync(store, signedBlock.Message.ParentRoot, slot).ConfigureAwait(false);
            }
            
            // Either root is the slot we want, so return it, or
            // root is older than queried slot, thus a skip slot. Return earliest root prior to slot
            return root;
        }
    }
}