// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution;

public sealed class ProtocolBalanceCleanup(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    IWorldState worldState,
    ISpecProvider specProvider,
    EezChainRules rules) : IBlockProcessor.IBlockTransactionsExecutor
{
    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        if (!rules.FinalizesProtocolBalances) Clear(block.Header);
        TxReceipt[] receipts = inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        if (rules.FinalizesProtocolBalances && block.Number != 0) Clear(block.Header);
        return receipts;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);

    public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();

    public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();

    public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);

    public long StartTxTimer() => inner.StartTxTimer();

    public void StopTxTimer(int i, long txStart) => inner.StopTxTimer(i, txStart);

    private void Clear(BlockHeader header)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        bool changed = false;
        foreach (Address address in rules.ProtocolBalanceAccounts)
        {
            UInt256 balance = worldState.GetBalance(address);
            if (!balance.IsZero)
            {
                worldState.SubtractFromBalance(address, balance, spec);
                changed = true;
            }
        }

        if (changed)
        {
            worldState.Commit(spec);
        }
    }
}
