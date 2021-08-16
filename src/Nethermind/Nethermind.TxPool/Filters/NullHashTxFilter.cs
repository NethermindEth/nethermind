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
    /// Filters out all the transactions without TX hash calculated.
    /// This generally should never happen as there should be no way for a transaction to be decoded
    /// without hash when coming from devp2p.
    /// </summary>
    internal class NullHashTxFilter : IIncomingTxFilter
    {
        public (bool Accepted, AddTxResult? Reason) Accept(Transaction tx, TxHandlingOptions handlingOptions)
        {
            if (tx.Hash is null)
            {
                return (false, AddTxResult.Invalid);
            }

            return (true, null);
        }
    }
}
