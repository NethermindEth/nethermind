// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions;

public class CompositeTxFilter(params ITxFilter[] txFilters) : ITxFilter
{
    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec currentSpec)
    {
        for (int i = 0; i < txFilters.Length; i++)
        {
            AcceptTxResult isAllowed = txFilters[i].IsAllowed(tx, parentHeader, currentSpec);
            if (!isAllowed)
            {
                return isAllowed;
            }
        }

        return AcceptTxResult.Accepted;
    }
}
