// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class LocalTxFilter(ISigner signer) : ITxFilter
    {
        private readonly ISigner _signer = signer;

        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context)
        {
            if (tx.SenderAddress == _signer.Address)
            {
                tx.IsServiceTransaction = true;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
