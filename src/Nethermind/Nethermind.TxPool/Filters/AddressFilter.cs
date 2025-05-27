// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

internal sealed class AddressFilter(
    HashSet<AddressAsKey> hashCache,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (hashCache.Contains(tx.SenderAddress!))
        {
            logger.Error($"Sender of the transaction is blacklisted. SenderAddress: {tx.SenderAddress!} TxHash: {tx.Hash}");
            Metrics.BlacklistedTransactions++;
            return AcceptTxResult.BlacklistedAddress;
        }

        if (tx.To is not null && hashCache.Contains(tx.To))
        {
            logger.Error($"To Address of the transaction is blacklisted. ToAddress: {tx.To} TxHash: {tx.Hash}");
            Metrics.BlacklistedTransactions++;
            return AcceptTxResult.BlacklistedAddress;
        }
        return AcceptTxResult.Accepted;
    }
}
