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
using Nethermind.TxPool.Comparison;

namespace Nethermind.TxPool.Collections
{
    public static class TxSortedPoolExtensions
    {
        public static IComparer<Transaction> GetPoolUniqueTxComparer(this IComparer<Transaction> comparer)
            => comparer
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not loose transactions we need to differentiate on their identity which provided comparer might not be doing

        public static IComparer<Transaction> GetPoolUniqueTxComparerByNonce(this IComparer<Transaction> comparer)
            => CompareTxByNonce.Instance // we need to ensure transactions are ordered by nonce, which might not be done in supplied comparer
                .ThenBy(GetPoolUniqueTxComparer(comparer));

        public static IComparer<Transaction> GetReplacementComparer(this IComparer<Transaction> comparer)
            => CompareReplacedTxByFee.Instance.ThenBy(comparer);

        public static Address? MapTxToGroup(this Transaction value) => value.SenderAddress;
    }
}
