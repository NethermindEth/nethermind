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

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Compares <see cref="Transaction"/>s based on <see cref="Transaction.Hash"/> identity. No two different signed transactions will be same.
    /// </summary>
    public class ByHashTxComparer : IComparer<Transaction>, IEqualityComparer<Transaction>
    {
        public static readonly ByHashTxComparer Instance = new();
        
        private ByHashTxComparer() { }

        public int Compare(Transaction? x, Transaction? y)
        {
            if (ReferenceEquals(x?.Hash, y?.Hash)) return 0;
            if (ReferenceEquals(null, y?.Hash)) return 1;
            if (ReferenceEquals(null, x?.Hash)) return -1;
            
            return x.Hash!.CompareTo(y.Hash);
        }

        public bool Equals(Transaction? x, Transaction? y) => Compare(x, y) == 0;

        public int GetHashCode(Transaction obj) => obj.Hash?.GetHashCode() ?? 0;
    }
}
