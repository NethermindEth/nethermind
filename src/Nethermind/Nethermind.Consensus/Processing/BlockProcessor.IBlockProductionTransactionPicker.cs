// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public interface IBlockProductionTransactionPicker
    {
        event EventHandler<AddingTxEventArgs>? AddingTransaction;

        AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
            IReadOnlySet<Transaction> transactionsInBlock, IWorldState stateProvider);
    }
}
