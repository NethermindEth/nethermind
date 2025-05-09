// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.WurdumTestPlugin;

public class WurdumRpcTxSource(ILogger logger) : ITxSource
{
    private IReadOnlyCollection<Transaction>? _transactions;

    public bool SupportsBlobs => false;

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
    {
        var transactions = _transactions ?? throw new InvalidOperationException("No transactions injected");
        _transactions = null;
        return transactions;
    }

    public void InjectTransactions(IReadOnlyCollection<Transaction> transactions)
    {
        if (_transactions is not null)
        {
            throw new InvalidOperationException("Tx source already have transactions injected");
        }

        // Explicitly ignore DepositTx as it interferes with the ArbitrumDeposit type
        List<Transaction> transactionsToInject = new();
        foreach (var transaction in transactions)
        {
            var shouldIgnore = !Enum.IsDefined(transaction.Type) || transaction.Type == TxType.DepositTx;

            if (transaction is IArbitrumTransaction arbitrumTransaction)
            {
                var inner = arbitrumTransaction.GetInner();
                logger.Info($"{(shouldIgnore ? "IGNORED" : "ADDED")} transaction {transaction.ToShortString()} of type {transaction.Type}:{inner.GetType()}");
                logger.Info(inner.ToString());
            }

            if (shouldIgnore)
            {
                continue;
            }

            transactionsToInject.Add(transaction);
        }

        _transactions = transactionsToInject;
        logger.Info($"{_transactions.Count} of {transactions.Count} injected transaction(s) added to the pool");
    }
}
