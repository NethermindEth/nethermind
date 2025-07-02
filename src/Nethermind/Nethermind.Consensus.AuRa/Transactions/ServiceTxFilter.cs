// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class ServiceTxFilter : ITxFilter
    {
        private readonly ISpecProvider _specProvider;

        public ServiceTxFilter(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            if (tx.IsZeroGasPrice(parentHeader, _specProvider))
            {
                tx.IsServiceTransaction = true;
            }

            return AcceptTxResult.Accepted;
        }
    }
}
