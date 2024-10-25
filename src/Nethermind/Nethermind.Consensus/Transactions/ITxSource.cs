// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    public interface ITxSource
    {
        IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, CancellationToken token = default);
    }
    public interface ITxSourceNotifier
    {
        event EventHandler<TxEventArgs> NewPendingTransactions;
        bool IsInterestingTx(Transaction tx, BlockHeader parent);
    }
}
