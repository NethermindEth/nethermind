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

using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BeaconBlockExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock, false));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock, bool forSigning)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            yield return item.StateRoot.ToSszBasicVector();
            yield return item.Body.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock);
            if (!forSigning)
            {
                //signature: BLSSignature
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
