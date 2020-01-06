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
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class VoluntaryExitExtensions
    {
        public static Hash32 SigningRoot(this VoluntaryExit item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this VoluntaryExit item)
        {
            return new SszContainer(GetValues(item, false));
        }

        public static SszList ToSszList(this IEnumerable<VoluntaryExit> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(VoluntaryExit item, bool forSigning)
        {
            yield return item.Epoch.ToSszBasicElement();
            yield return item.ValidatorIndex.ToSszBasicElement();
            if (!forSigning)
            {
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
