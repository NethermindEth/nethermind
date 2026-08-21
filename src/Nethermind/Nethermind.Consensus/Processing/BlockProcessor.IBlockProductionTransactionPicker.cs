// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public interface IBlockProductionTransactionPicker
    {
        event EventHandler<AddingTxEventArgs>? AddingTransaction;

        AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
            IReadOnlySet<Transaction> transactionsInBlock, IReadOnlyStateProvider stateProvider);

        // EIP-8037: per-dimension block gas; the default bridges to the legacy member so existing implementors keep binding.
        AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx,
            IReadOnlySet<Transaction> transactionsInBlock, IReadOnlyStateProvider stateProvider,
            ulong cumulativeBlockExecutionGas, ulong cumulativeBlockStateGas)
            => CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);
    }
}
