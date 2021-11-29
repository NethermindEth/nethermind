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

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        public class TransactionsInBlock : ICollection<Transaction>, IReadOnlySet<Transaction>
        {
            private readonly LinkedHashSet<Transaction> _innerSet = new(ByHashTxComparer.Instance);

            public void Add(Transaction item)
            {
                if (_innerSet.Add(item))
                {
                    CallDataLength += item.DataLength;
                }
            }
            
            public bool Remove(Transaction item)
            {
                if (_innerSet.Remove(item))
                {
                    CallDataLength -= item.DataLength;
                    return true;
                }

                return false;
            }

            public void Clear()
            {
                CallDataLength = 0;
                _innerSet.Clear();
            }

            private long CallDataLength { get; set; }

            public bool CanAddTx(bool isEip4488Enabled, Transaction tx) =>
                !isEip4488Enabled || 
                CallDataLength + tx.DataLength <= Block.BaseMaxCallDataPerBlock + Count * Transaction.CallDataPerTxStipend + Transaction.CallDataPerTxStipend;
            
            public IEnumerator<Transaction> GetEnumerator() => _innerSet.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_innerSet).GetEnumerator();
            public bool Contains(Transaction item) => _innerSet.Contains(item);
            public bool IsProperSubsetOf(IEnumerable<Transaction> other) => _innerSet.IsProperSubsetOf(other);
            public bool IsProperSupersetOf(IEnumerable<Transaction> other) => _innerSet.IsProperSupersetOf(other);
            public bool IsSubsetOf(IEnumerable<Transaction> other) => _innerSet.IsSubsetOf(other);
            public bool IsSupersetOf(IEnumerable<Transaction> other) => _innerSet.IsSupersetOf(other);
            public bool Overlaps(IEnumerable<Transaction> other) => _innerSet.Overlaps(other);
            public bool SetEquals(IEnumerable<Transaction> other) => _innerSet.SetEquals(other);
            public void CopyTo(Transaction[] array, int arrayIndex) => _innerSet.CopyTo(array, arrayIndex);
            public int Count => _innerSet.Count;
            public bool IsReadOnly => _innerSet.IsReadOnly;
        }
    }
}
