// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.TxPool;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(
    ITxPool txPool)
    : IHandler<Transaction[]>
{
    public ResultWrapper<Transaction[]> Handle()
    {
        Transaction[] txs = txPool.GetPendingTransactions();
        return ResultWrapper<Transaction[]>.Success(txs);
    }
}
