// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>Marks completion of a requested transaction without changing the block presented to tracers.</summary>
internal sealed class TransactionTraceBoundary(IBlockTracer inner, Hash256 transactionHash) : IBlockTracer
{
    private bool _isTarget;
    internal bool IsComplete { get; private set; }
    internal IBlockTracer Inner => inner;

    internal static IBlockTracer Wrap(IBlockTracer tracer, Hash256? transactionHash) =>
        transactionHash is null || tracer.IsTracingRewards ? tracer : new TransactionTraceBoundary(tracer, transactionHash);

    internal static TransactionTraceBoundary? Get(IBlockTracer tracer, ProcessingOptions options) =>
        options.ContainsFlag(ProcessingOptions.Trace)
        && (options & (ProcessingOptions.StoreReceipts | ProcessingOptions.ForceSameBlock)) == 0
            ? tracer as TransactionTraceBoundary : null;

    public bool IsTracingRewards => inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        IsComplete = false;
        _isTarget = false;
        inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _isTarget = tx?.Hash == transactionHash;
        return inner.StartNewTxTrace(tx);
    }

    public void EndTxTrace()
    {
        inner.EndTxTrace();
        IsComplete |= _isTarget;
        _isTarget = false;
    }

    public void EndBlockTrace() => inner.EndBlockTrace();
}
