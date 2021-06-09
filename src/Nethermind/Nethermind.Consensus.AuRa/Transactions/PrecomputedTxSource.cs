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
    /// <summary>
    /// <see cref="ITxSource"/> that precomputes some transactions before block processing lazily other transaction.
    /// </summary>
    /// <remarks>
    /// Rationale: Posdao transactions need to be precomputed before any block processing is done as AuRa FinalizeChange might be called before computing lazy transactions.
    /// </remarks>
    public class PrecomputedTxSource : ITxSource, IDisposable
    {
        private readonly ITxSource _innerSource;
        private readonly Predicate<Transaction> _shouldPrecompute;
        private IEnumerator<Transaction>? _enumerator;
        private readonly IList<Transaction> _precomputedTransactions = new List<Transaction>();

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="innerSource">inner wrapped source</param>
        /// <param name="shouldPrecompute">predicate if current transaction is to be precomputed</param>
        /// <remarks>Only first transactions will be precomputed as soon as first lazy transaction will be found precomputing will be stopped</remarks>
        public PrecomputedTxSource(ITxSource innerSource, Predicate<Transaction> shouldPrecompute)
        {
            _innerSource = innerSource;
            _shouldPrecompute = shouldPrecompute;
        }

        public void PrecomputeTransactions(BlockHeader parent, long gasLimit)
        {
            _enumerator?.Dispose();
            _precomputedTransactions.Clear();
            _enumerator = _innerSource.GetTransactions(parent, gasLimit).GetEnumerator();

            while (_enumerator.MoveNext())
            {
                Transaction currentTx = _enumerator.Current;
                if (currentTx is not null)
                {
                    if (_shouldPrecompute(currentTx))
                    {
                        _precomputedTransactions.Add(currentTx);
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
            for (int i = 0; i < _precomputedTransactions.Count; i++)
            {
                yield return _precomputedTransactions[i];
            }

            if (_enumerator is not null)
            {
                if (_enumerator.Current is not null)
                {
                    if (!_shouldPrecompute(_enumerator.Current))
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
            
        public override string ToString() => $"{nameof(PrecomputedTxSource)} [ {_innerSource} ]";
            
        public void Dispose()
        {
            _enumerator?.Dispose();
        }
    }
}
