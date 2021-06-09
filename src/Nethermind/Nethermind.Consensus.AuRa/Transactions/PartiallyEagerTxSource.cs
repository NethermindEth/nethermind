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
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class PartiallyEagerTxSource : ITxSource, IDisposable
    {
        private readonly ITxSource _innerSource;
        private readonly Predicate<Transaction> _eagerTransaction;
        private IEnumerator<Transaction>? _enumerator;
        private readonly IList<Transaction> _eagerTransactions = new List<Transaction>();

        public PartiallyEagerTxSource(ITxSource innerSource, Predicate<Transaction> eagerTransaction)
        {
            _innerSource = innerSource;
            _eagerTransaction = eagerTransaction;
        }

        public void PrepareEagerTransactions(BlockHeader parent, long gasLimit)
        {
            _enumerator?.Dispose();
            _eagerTransactions.Clear();
            _enumerator = _innerSource.GetTransactions(parent, gasLimit).GetEnumerator();

            while (_enumerator.MoveNext())
            {
                Transaction currentTx = _enumerator.Current;
                if (currentTx is not null)
                {
                    if (_eagerTransaction(currentTx))
                    {
                        _eagerTransactions.Add(currentTx);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            for (int i = 0; i < _eagerTransactions.Count; i++)
            {
                yield return _eagerTransactions[i];
            }

            if (_enumerator is not null)
            {
                if (_enumerator.Current is not null)
                {
                    if (!_eagerTransaction(_enumerator.Current))
                    {
                        yield return _enumerator.Current;
                    }
                }

                while (_enumerator.MoveNext())
                {
                    if (_enumerator.Current is not null)
                    {
                        yield return _enumerator.Current;
                    }
                }
                    
                _enumerator.Dispose();
            }
        }
            
        public override string ToString() => $"{nameof(PartiallyEagerTxSource)} [ {_innerSource} ]";
            
        public void Dispose()
        {
            _enumerator?.Dispose();
        }
    }
}
