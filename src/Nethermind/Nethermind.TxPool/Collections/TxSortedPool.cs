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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.TxPool.Collections
{
    public class TxSortedPool : SortedPool<Keccak, Transaction, Address>
    {
        public TxSortedPool(int capacity, IComparer<Transaction> comparer = null)
            : base(capacity, comparer ?? CompareTxByGas.Instance)
        {
        }

        protected override IComparer<Transaction> GetComparerWithIdentity(IComparer<Transaction> comparer) => GetTxComparerWithIdentity(comparer);

        protected override Address MapToGroup(Transaction value) => MapTxToGroup(value);

        internal static IComparer<Transaction> GetTxComparerWithIdentity(IComparer<Transaction> comparer)
            => CompareTxByNonce.Instance // we need to ensure transactions are ordered by nonce, which might not be done in supplied comparer
                .ThenBy(comparer)
                .ThenBy(CompareTxByHash.Instance); // in order to sort properly and not loose transactions we need to differentiate on their identity which provided comparer might not be doing

        internal static Address MapTxToGroup(Transaction value) => value.SenderAddress;
    }
}
