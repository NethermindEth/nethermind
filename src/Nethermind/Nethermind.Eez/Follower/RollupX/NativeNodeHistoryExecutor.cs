// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeNodeHistoryExecutor(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    IWorldState state,
    ISpecProvider specs,
    EezChainRules rules,
    NativeCanonicalHistory history,
    NativeL1HeaderChain? captured = null) : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly NativeHistorySchedule _schedule = new(rules.Schedule!.GenesisTimestamp, rules.Schedule.BlockIntervalSeconds,
        rules.Schedule.SyncIntervalBlocks, rules.NativeHistoryOffset);

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions options, BlockReceiptsTracer tracer, CancellationToken token = default)
    {
        NativeHistoryBlockExecutor execution = new(inner, state, specs, rules, _schedule, rules.Schedule!.L1Reference.Number,
            null, captured ?? history.Read());
        return execution.ProcessTransactions(block, options, tracer, token);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);
    public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();
    public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();
    public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);
    public long StartTxTimer() => inner.StartTxTimer();
    public void StopTxTimer(int index, long start) => inner.StopTxTimer(index, start);
}
