// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class CompareTxSameSenderNonce(
        IComparer<MempoolTransaction> sameSenderNoncePriorityComparer,
        IComparer<MempoolTransaction> differentSenderNoncePriorityComparer) : IComparer<MempoolTransaction>
    {
        private readonly IComparer<MempoolTransaction> _sameSenderNoncePriorityComparer = sameSenderNoncePriorityComparer;
        private readonly IComparer<MempoolTransaction> _differentSenderNoncePriorityComparer = differentSenderNoncePriorityComparer;

        public int Compare(MempoolTransaction? x, MempoolTransaction? y)
        {
            IComparer<MempoolTransaction> firstComparer = _differentSenderNoncePriorityComparer;
            IComparer<MempoolTransaction> secondComparer = _sameSenderNoncePriorityComparer;

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
