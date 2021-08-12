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

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxSameSenderNonce : IComparer<Transaction>
    {
        private readonly IComparer<Transaction> _sameSenderNoncePriorityComparer;
        private readonly IComparer<Transaction> _differentSenderNoncePriorityComparer;

        public CompareTxSameSenderNonce(
            IComparer<Transaction> sameSenderNoncePriorityComparer, 
            IComparer<Transaction> differentSenderNoncePriorityComparer)
        {
            _sameSenderNoncePriorityComparer = sameSenderNoncePriorityComparer;
            _differentSenderNoncePriorityComparer = differentSenderNoncePriorityComparer;
        }
            
        public int Compare(Transaction? x, Transaction? y)
        {
            IComparer<Transaction> firstComparer = _differentSenderNoncePriorityComparer;
            IComparer<Transaction> secondComparer = _sameSenderNoncePriorityComparer;

            bool sameNonceAndSender = Equals(x?.Nonce, y?.Nonce) && Equals(x?.SenderAddress, y?.SenderAddress);
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
