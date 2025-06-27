// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using System.Linq;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public class CompositeTxFilter : ITxFilter
    {
        private readonly ITxFilter[] _txFilters;

        public CompositeTxFilter(params ITxFilter?[] txFilters)
        {
            _txFilters = txFilters.Where(static f => f is not null).ToArray();
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
        {
            for (int i = 0; i < _txFilters.Length; i++)
            {
                AcceptTxResult isAllowed = _txFilters[i].IsAllowed(tx, parentHeader, currentSpec);
                if (!isAllowed)
                {
                    return isAllowed;
                }
            }

            return AcceptTxResult.Accepted;
        }
    }
}
