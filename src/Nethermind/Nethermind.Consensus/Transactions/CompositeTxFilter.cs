// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public class CompositeTxFilter : ITxFilter
    {
        private readonly ITxFilter[] _txFilters;

        public CompositeTxFilter(params ITxFilter[] txFilters)
        {
            _txFilters = txFilters?.Where(f => f is not null).ToArray() ?? Array.Empty<ITxFilter>();
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            for (int i = 0; i < _txFilters.Length; i++)
            {
                AcceptTxResult isAllowed = _txFilters[i].IsAllowed(tx, parentHeader);
                if (!isAllowed)
                {
                    return isAllowed;
                }
            }

            return AcceptTxResult.Accepted;
        }
    }
}
