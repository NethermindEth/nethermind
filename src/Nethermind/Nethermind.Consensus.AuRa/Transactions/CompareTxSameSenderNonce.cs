// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
