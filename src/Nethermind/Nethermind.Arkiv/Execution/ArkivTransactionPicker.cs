// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// Which candidates a block being built tries. Only what is not Arkiv's to decide is checked here — room left in the
/// block, a sender, no repeat. Nonce, balance and fees are Arkiv's executor's call when the transaction is applied, as
/// in arkiv-reth's builder: the account trie here is mirrored once per block, so it cannot answer for a sender's
/// second transaction in the block, and the protocol purge (its nonce the block number, its sender holding nothing)
/// would fail both checks it never takes.
/// </summary>
public sealed class ArkivTransactionPicker : BlockProcessor.IBlockProductionTransactionPicker
{
    public event EventHandler<BlockProcessor.AddingTxEventArgs>? AddingTransaction;

    public BlockProcessor.AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx, IReadOnlySet<Transaction> transactionsInBlock,
        IReadOnlyStateProvider stateProvider, ulong cumulativeBlockExecutionGas, ulong cumulativeBlockStateGas)
    {
        BlockProcessor.AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);
        ulong remaining = block.Header.GasLimit > cumulativeBlockExecutionGas ? block.Header.GasLimit - cumulativeBlockExecutionGas : 0;
        if (remaining < GasCostOf.Transaction) return args.Set(BlockProcessor.TxAction.Stop, "Block full");
        if (currentTx.GasLimit > remaining) return args.Set(BlockProcessor.TxAction.Skip, $"Not enough gas in block, gas limit {currentTx.GasLimit} > {remaining}");
        if (currentTx.SenderAddress is null) return args.Set(BlockProcessor.TxAction.Skip, "Null sender");
        if (transactionsInBlock.Contains(currentTx)) return args.Set(BlockProcessor.TxAction.Skip, "Transaction already in block");
        AddingTransaction?.Invoke(this, args);
        return args;
    }
}
