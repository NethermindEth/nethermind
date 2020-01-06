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
    public static class DepositDataExtensions
    {
        public static Hash32 HashTreeRoot(this IEnumerable<DepositData> list, ulong limit)
        {
            var tree = new SszTree(list.ToSszList(limit));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 HashTreeRoot(this DepositData item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this DepositData item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this DepositData item)
        {
            return new SszContainer(GetValues(item, false));
        }

        public static SszList ToSszList(this IEnumerable<DepositData> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(DepositData item, bool forSigning)
        {
            yield return new SszBasicVector(item.PublicKey.AsSpan());
            yield return new SszBasicVector(item.WithdrawalCredentials.AsSpan());
            yield return new SszBasicElement((ulong)item.Amount);
            if (!forSigning)
            {
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
