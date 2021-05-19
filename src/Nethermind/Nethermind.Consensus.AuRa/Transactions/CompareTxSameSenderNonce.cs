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
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxSameSenderNonce : IComparer<WrappedTransaction>
    {
        private readonly IComparer<WrappedTransaction> _sameSenderNoncePriorityComparer;
        private readonly IComparer<WrappedTransaction> _differentSenderNoncePriorityComparer;

        public CompareTxSameSenderNonce(
            IComparer<WrappedTransaction> sameSenderNoncePriorityComparer, 
            IComparer<WrappedTransaction> differentSenderNoncePriorityComparer)
        {
            _sameSenderNoncePriorityComparer = sameSenderNoncePriorityComparer;
            _differentSenderNoncePriorityComparer = differentSenderNoncePriorityComparer;
        }
            
        public int Compare(WrappedTransaction? x, WrappedTransaction? y)
        {
            IComparer<WrappedTransaction> firstComparer = _differentSenderNoncePriorityComparer;
            IComparer<WrappedTransaction> secondComparer = _sameSenderNoncePriorityComparer;

            bool sameNonceAndSender = Equals(x?.Tx?.Nonce, y?.Tx?.Nonce) && Equals(x?.Tx?.SenderAddress, y?.Tx?.SenderAddress);
            if (sameNonceAndSender)
            {
                
                firstComparer = _sameSenderNoncePriorityComparer;
                secondComparer = _differentSenderNoncePriorityComparer;
            }

            int result = firstComparer.Compare(x, y);
            return result != 0 ? result : secondComparer.Compare(x, y);
        }
    }
}
