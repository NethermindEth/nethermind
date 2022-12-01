// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class BeaconBlockHeaderExtensions
    {
        public static Root HashTreeRoot(this BeaconBlockHeader item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item)));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlockHeader item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockHeader item)
        {
            //slot: Slot
            yield return new SszBasicElement((ulong)item.Slot);
            //parent_root: Hash
            yield return item.ParentRoot.ToSszBasicVector();
            //state_root: Hash
            yield return item.StateRoot.ToSszBasicVector();
            //body_root: Hash
            yield return item.BodyRoot.ToSszBasicVector();
        }
    }
}
