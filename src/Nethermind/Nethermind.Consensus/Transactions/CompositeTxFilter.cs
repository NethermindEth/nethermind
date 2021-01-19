//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions
{
    public class CompositeTxFilter : ITxFilter
    {
        private readonly ITxFilter[] _txFilters;

        public CompositeTxFilter(params ITxFilter[] txFilters)
        {
            _txFilters = txFilters?.Where(f => f != null).ToArray() ?? Array.Empty<ITxFilter>();
        }
        
        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            for (int i = 0; i < _txFilters.Length; i++)
            {
                (bool, string) result = _txFilters[i].IsAllowed(tx, parentHeader);
                if (!result.Item1)
                {
                    return result;
                }
            }

            return (true, string.Empty);
        }
    }
}
