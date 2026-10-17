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

/// <summary>
/// Zeroes the balances <see cref="EezChainRules.BurnedAtBlockStart"/> names once the pre-execution system calls are
/// committed and before the first transaction, in every block. An absent or empty-balance account is left alone; one
/// that becomes empty is removed under EIP-161, as any touched empty account is.
/// </summary>
public sealed class BlockStartBurn(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    IWorldState worldState,
    ISpecProvider specProvider,
    EezChainRules rules) : IBlockProcessor.IBlockTransactionsExecutor
{
    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        Burn(block.Header);
        return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);

    public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();

    public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();

    public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);

    public long StartTxTimer() => inner.StartTxTimer();

    public void StopTxTimer(int i, long txStart) => inner.StopTxTimer(i, txStart);

    private void Burn(BlockHeader header)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        bool burned = false;
        foreach (Address address in rules.BurnedAtBlockStart)
        {
            UInt256 balance = worldState.GetBalance(address);
            if (!balance.IsZero)
            {
                worldState.SubtractFromBalance(address, balance, spec);
                burned = true;
            }
        }

        if (burned)
        {
            worldState.Commit(spec);
        }
    }
}
