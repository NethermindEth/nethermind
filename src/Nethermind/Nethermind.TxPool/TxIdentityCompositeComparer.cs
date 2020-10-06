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
// 

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    /// <summary>
    /// This comparer uses inner comparer to do comparision, but when Transactions are same it defaults to <see cref="Transaction.Hash"/> comparision to differentiate between transactions  
    /// </summary>
    public class TxIdentityCompositeComparer : IComparer<Transaction>
    {
        private readonly IComparer<Transaction> _innerComparer;

        public TxIdentityCompositeComparer(IComparer<Transaction> innerComparer)
        {
            _innerComparer = innerComparer;
        }

        public int Compare(Transaction x, Transaction y)
        {
            var comparision = _innerComparer.Compare(x, y);
            if (comparision != 0 || x == null || y == null) return comparision;
            return x.Hash.CompareTo(y.Hash);
        }
    }
}
