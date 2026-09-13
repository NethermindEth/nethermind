// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>Marks completion of a requested transaction without changing the block presented to tracers.</summary>
public sealed class TransactionTraceBoundary : IBlockTracer
{
    private readonly IBlockTracer _inner;
    private readonly Hash256 _transactionHash;

    private TransactionTraceBoundary(IBlockTracer inner, Hash256 transactionHash)
    {
        _inner = inner;
        _transactionHash = transactionHash;
    }

    private bool _isTarget;
    internal bool IsComplete { get; private set; }
    internal IBlockTracer Inner => _inner;

    /// <summary>Wraps a transaction tracer for early completion in a supported read-only replay environment.</summary>
    /// <param name="tracer">The tracer to forward callbacks to; reward tracing retains full replay.</param>
    /// <param name="transactionHash">The transaction to stop after, or null for unrestricted replay.</param>
    /// <returns>The original tracer for a null hash or reward tracing; otherwise a completion boundary.</returns>
    public static IBlockTracer Wrap(IBlockTracer tracer, Hash256? transactionHash) =>
        transactionHash is null || tracer.IsTracingRewards ? tracer : new TransactionTraceBoundary(tracer, transactionHash);

    internal static TransactionTraceBoundary? Get(IBlockTracer tracer, ProcessingOptions options) =>
        options.ContainsFlag(ProcessingOptions.Trace)
        && (options & (ProcessingOptions.StoreReceipts | ProcessingOptions.ForceSameBlock)) == 0
            ? tracer as TransactionTraceBoundary : null;

    public bool IsTracingRewards => _inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        IsComplete = false;
        _isTarget = false;
        _inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _isTarget = tx?.Hash == _transactionHash;
        return _inner.StartNewTxTrace(tx);
    }

    public void EndTxTrace()
    {
        _inner.EndTxTrace();
        IsComplete |= _isTarget;
        _isTarget = false;
    }

    public void EndBlockTrace() => _inner.EndBlockTrace();
}
