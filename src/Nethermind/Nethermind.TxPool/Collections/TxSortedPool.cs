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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.TxPool.Collections
{
    public class TxSortedPool : SortedPool<Keccak, WrappedTransaction, Address>
    {
        public TxSortedPool(int capacity, IComparer<WrappedTransaction> comparer)
            : base(capacity, comparer)
        {
        }

        protected override IComparer<WrappedTransaction> GetUniqueComparer(IComparer<WrappedTransaction> comparer) => GetPoolUniqueTxComparer(comparer);
        protected override IComparer<WrappedTransaction> GetGroupComparer(IComparer<WrappedTransaction> comparer) => GetPoolUniqueTxComparerByNonce(comparer);

        protected override Address? MapToGroup(WrappedTransaction value) => MapTxToGroup(value);

        internal static IComparer<WrappedTransaction> GetPoolUniqueTxComparer(IComparer<WrappedTransaction> comparer)
            => comparer
                .ThenBy(DistinctCompareTx.Instance); // in order to sort properly and not loose transactions we need to differentiate on their identity which provided comparer might not be doing

        internal static IComparer<WrappedTransaction> GetPoolUniqueTxComparerByNonce(IComparer<WrappedTransaction> comparer)
            => CompareTxByNonce.Instance // we need to ensure transactions are ordered by nonce, which might not be done in supplied comparer
                .ThenBy(GetPoolUniqueTxComparer(comparer));

        internal static Address? MapTxToGroup(WrappedTransaction value) => value.Tx.SenderAddress;
    }
}
