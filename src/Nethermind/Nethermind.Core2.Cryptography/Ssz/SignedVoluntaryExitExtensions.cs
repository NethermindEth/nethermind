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

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class SignedVoluntaryExitExtensions
    {
        public static Root HashTreeRoot(this SignedVoluntaryExit item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item)));
            return new Root(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this SignedVoluntaryExit item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<SignedVoluntaryExit> list, ulong limit)
        {
            return new SszList(list.Select(x => ToSszContainer(x)), limit);
        }

        private static IEnumerable<SszElement> GetValues(SignedVoluntaryExit item)
        {
            yield return item.Message.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
