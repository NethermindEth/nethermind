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
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class CrosslinkExtensions
    {
        public static Hash32 HashTreeRoot(this Crosslink item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this Crosslink item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszVector ToSszVector(this IEnumerable<Crosslink> vector)
        {
            return new SszVector(vector.Select(x => x.ToSszContainer()));
        }

        private static IEnumerable<SszElement> GetValues(Crosslink item)
        {
            yield return item.Shard.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            // Crosslinking data
            yield return item.StartEpoch.ToSszBasicElement();
            yield return item.EndEpoch.ToSszBasicElement();
            yield return item.DataRoot.ToSszBasicVector();
        }
    }
}
