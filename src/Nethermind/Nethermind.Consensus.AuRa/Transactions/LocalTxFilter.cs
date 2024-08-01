// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class LocalTxFilter : ITxFilter
    {
        private readonly ISigner _signer;

        public LocalTxFilter(ISigner signer)
        {
            _signer = signer;
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            if (tx.SenderAddress == _signer.Address)
            {
                tx.IsServiceTransaction = true;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
