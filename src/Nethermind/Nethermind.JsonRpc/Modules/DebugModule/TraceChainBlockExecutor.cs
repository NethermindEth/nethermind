// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State.OverridableEnv;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed class TraceChainBlockExecutor(
    IOverridableEnv<GethStyleTracer.BlockProcessingComponents> environment,
    ISpecProvider specProvider)
{
    internal TraceChainTransaction?[] Trace(Block block, TraceChainOptions options, CancellationToken token)
    {
        using Scope<GethStyleTracer.BlockProcessingComponents> scope = environment.BuildAndOverrideAtTarget(block.Header);
        if (block.Transactions.Length == 0) return [];
        BlockLogIndex logIndex = new();
        GethTraceCancellation cancellation = new() { Token = token };
        GethTraceOptions tracerOptions = options.ToTraceOptions() with { ExecutionCancellation = cancellation };
        IBlockTracer<GethLikeTxTrace> inner;
        try
        {
            inner = GethStyleTracer.CreateOptionsTracer(block.Header, tracerOptions, scope.Component.WorldState, specProvider, (_, _) => logIndex);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            TraceChainTransaction?[] failed = new TraceChainTransaction?[block.Transactions.Length];
            failed[0] = new(block.Transactions[0].Hash!, null, exception.Message);
            return failed;
        }
        using TraceChainBlockTracer tracer = new(block, inner, options, token, cancellation);
        Exception? failure = null;
        try
        {
            scope.Component.BlockchainProcessor.Process(block, TraceProcessingOptions.ReadOnlyReplay,
                TransactionTraceBoundary.Wrap(tracer, block.Transactions[^1].Hash), token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && tracer.TransactionIndex >= 0)
        {
            // Geth traceChain stops at the first failed transaction, preserving prior results and null tail entries.
            failure = tracer.TransactionTimedOut ? new TimeoutException("execution timeout") : exception;
        }
        return tracer.TakeResult(failure);
    }
}

internal sealed class TraceChainBlockTracer(Block block, IBlockTracer<GethLikeTxTrace> inner,
    TraceChainOptions? options = null, CancellationToken token = default, GethTraceCancellation? cancellation = null) : IBlockTracer, IDisposable
{
    private GethTraceDeadline? _transactionCancellation;
    private int _nextTransaction;
    private bool _transferred;
    internal int TransactionIndex { get; private set; } = -1;
    internal bool TransactionTimedOut => _transactionCancellation?.Expired == true;

    public bool IsTracingRewards => inner.IsTracingRewards;

    public void StartNewBlockTrace(Block tracedBlock) => inner.StartNewBlockTrace(tracedBlock);

    public ITxTracer StartNewTxTrace(Transaction? transaction)
    {
        token.ThrowIfCancellationRequested();
        TransactionIndex = transaction is null ? -1 : _nextTransaction++;
        _transactionCancellation = new(token);
        if (cancellation is not null) cancellation.Token = _transactionCancellation.Token;
        ITxTracer tracer = inner.StartNewTxTrace(transaction);
        _transactionCancellation.Start(options?.ParseTimeout() ?? TimeSpan.FromSeconds(5));
        return tracer.WithCancellation(_transactionCancellation.Token);
    }

    public void EndTxTrace()
    {
        _transactionCancellation?.Token.ThrowIfCancellationRequested();
        inner.EndTxTrace();
        _transactionCancellation?.Dispose();
        _transactionCancellation = null;
        TransactionIndex = -1;
    }

    public void EndBlockTrace() => inner.EndBlockTrace();

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => inner.ReportReward(author, rewardType, rewardValue);

    internal TraceChainTransaction?[] TakeResult(Exception? failure)
    {
        TraceChainTransaction?[] result = new TraceChainTransaction?[block.Transactions.Length];
        int index = 0;
        foreach (GethLikeTxTrace trace in inner.BuildResult())
        {
            result[index] = new(block.Transactions[index].Hash!, trace, null);
            index++;
        }
        if (failure is not null)
            result[TransactionIndex] = new(block.Transactions[TransactionIndex].Hash!, null, failure.Message);
        _transferred = true;
        return result;
    }

    public void Dispose()
    {
        _transactionCancellation?.Dispose();
        if (!_transferred)
            foreach (GethLikeTxTrace trace in inner.BuildResult()) trace.Dispose();
        inner.TryDispose();
    }
}
