// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
