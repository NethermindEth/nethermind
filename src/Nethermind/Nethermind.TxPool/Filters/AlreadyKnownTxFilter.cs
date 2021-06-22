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

using Nethermind.Core;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// This filters out transactions that have already been analyzed in the current scope
    /// (block scope for rejected transactions or chain scope for accepted transactions).
    /// It uses a limited capacity hash cache underneath so there is no strict promise on filtering
    /// transactions.
    /// </summary>
    internal class AlreadyKnownTxFilter : IIncomingTxFilter
    {
        private readonly HashCache _hashCache;

        public AlreadyKnownTxFilter(HashCache hashCache)
        {
            _hashCache = hashCache;
        }

        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            if (_hashCache.Get(tx.Hash!))
            {
                Metrics.PendingTransactionsKnown++;
                return (false, AddTxResult.AlreadyKnown);
            }

            _hashCache.SetForCurrentBlock(tx.Hash!);

            return (true, null);
        }
    }
}
