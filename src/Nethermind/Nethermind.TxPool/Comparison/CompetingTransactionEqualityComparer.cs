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

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool.Comparison
{
    /// <summary>
    /// Comparer to check if two pending <see cref="Transaction"/>s compete with each other.
    /// <see cref="Transaction"/>s compete with each other if they have same <see cref="Transaction.SenderAddress"/> and <see cref="Transaction.Nonce"/>. In that case only one transaction can go into chain. 
    /// </summary>
    public class CompetingTransactionEqualityComparer : IEqualityComparer<Transaction?>
    {
        public static readonly CompetingTransactionEqualityComparer Instance = new();
        
        private CompetingTransactionEqualityComparer() { }
        
        public bool Equals(Transaction? x, Transaction? y) =>
            ReferenceEquals(x, y) || !ReferenceEquals(x, null) && !ReferenceEquals(y, null) && x.SenderAddress == y.SenderAddress && x.Nonce == y.Nonce;

        public int GetHashCode(Transaction? obj) => HashCode.Combine(obj?.SenderAddress, obj?.Nonce);
    }
}
