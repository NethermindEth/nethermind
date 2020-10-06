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
    public partial class TxPool
    {
        public class DefaultTxPoolComparer : IComparer<Transaction>
        {
            public static readonly DefaultTxPoolComparer Instance = new DefaultTxPoolComparer();
            
            public int Compare(Transaction x, Transaction y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (ReferenceEquals(null, y)) return 1;
                if (ReferenceEquals(null, x)) return -1;
                
                // compare by nonce ascending
                int nonceComparison = x.Nonce.CompareTo(y.Nonce);
                if (nonceComparison != 0) return nonceComparison;
                
                // then by gas price descending
                int gasPriceComparison = y.GasPrice.CompareTo(x.GasPrice);
                if (gasPriceComparison != 0) return gasPriceComparison;
                
                // then by gas limit ascending
                return x.GasLimit.CompareTo(y.GasLimit);
            }
        }
    }
}
