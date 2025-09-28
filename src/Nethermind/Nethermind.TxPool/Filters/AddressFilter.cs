// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

internal sealed class AddressFilter(
    HashSet<AddressAsKey> toAddressCache,
    HashSet<AddressAsKey> fromAddressCache,
    ILogger logger) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (fromAddressCache.Contains(tx.SenderAddress!))
        {
            logger.Error(
                $"Submitted transaction:{tx.Hash} has a blocked SENDER. Blocked Address: {tx.SenderAddress}. Full Sender: {tx.SenderAddress}, Recipient: {tx.To}.");
            Metrics.BlacklistedTransactions++;
            return AcceptTxResult.BlacklistedAddress;
        }

        if (tx.To is not null && toAddressCache.Contains(tx.To))
        {
            logger.Error(
                $"Submitted transaction:{tx.Hash} has a blocked RECIPIENT. Blocked Address: {tx.To}. Full Sender: {tx.SenderAddress}, Recipient: {tx.To}.");
            Metrics.BlacklistedTransactions++;
            return AcceptTxResult.BlacklistedAddress;
        }
        return AcceptTxResult.Accepted;
    }
}
