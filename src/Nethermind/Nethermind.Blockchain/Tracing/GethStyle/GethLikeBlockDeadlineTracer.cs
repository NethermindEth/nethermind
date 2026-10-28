// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethLikeBlockDeadlineTracer(
    IBlockTracer<GethLikeTxTrace> inner, GethTraceOptions options, CancellationToken external)
    : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    private CancellationToken _external = external;
    private GethTraceDeadline? _deadline;
    private bool _transferred;
    private bool _disposed;

    internal bool Expired => _deadline?.Expired == true;
    public bool IsTracingRewards => inner.IsTracingRewards;

    internal static IBlockTracer<GethLikeTxTrace> Wrap(IBlockTracer<GethLikeTxTrace> tracer, GethTraceOptions options, CancellationToken external) =>
        options.EnforceExecutionDeadline && (string.IsNullOrEmpty(options.Tracer) || GethLikeNativeTracerFactory.IsNativeTracer(options.Tracer))
            ? new GethLikeBlockDeadlineTracer(tracer, options, external)
            : tracer;

    // A second cancellation wrapper would hide the transaction's IsCancelled from VM dispatch.
    internal static IBlockTracer WithCancellation(IBlockTracer tracer, CancellationToken external)
    {
        if (tracer is not GethLikeBlockDeadlineTracer deadline) return tracer.WithCancellation(external);
        deadline._external = external;
        return deadline;
    }

    public void StartNewBlockTrace(Block block)
    {
        _external.ThrowIfCancellationRequested();
        inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _external.ThrowIfCancellationRequested();
        ITxTracer current = inner.StartNewTxTrace(tx);
        if (tx is null || options.TxHash is { } target && target != tx.Hash)
            return current.WithCancellation(_external);

        try
        {
            TimeSpan duration = options.ResolveTimeout() ?? GethTraceDeadline.DefaultTimeout;
            _deadline = new(_external, options.DeadlineClock);
            _deadline.Start(duration);
            CheckDeadline();
            return current.WithCancellation(_deadline.Token);
        }
        catch (Exception exception)
        {
            try
            {
                try { current.Dispose(); }
                finally { ReleaseDeadline(); }
            }
            catch (Exception cleanupException)
            {
                exception.Data[nameof(GethLikeBlockDeadlineTracer)] = cleanupException;
            }
            throw;
        }
    }

    public void EndTxTrace()
    {
        try
        {
            CheckDeadline();
            inner.EndTxTrace();
            CheckDeadline();
        }
        catch (OperationCanceledException) when (Expired)
        {
            throw new GethTraceDeadlineException();
        }
        finally
        {
            ReleaseDeadline();
        }
    }

    private void CheckDeadline()
    {
        _external.ThrowIfCancellationRequested();
        if (Expired) throw new GethTraceDeadlineException();
    }

    public void EndBlockTrace() => inner.EndBlockTrace();

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        _external.ThrowIfCancellationRequested();
        inner.ReportReward(author, rewardType, rewardValue);
    }

    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        CheckDeadline();
        IReadOnlyCollection<GethLikeTxTrace> result = inner.BuildResult();
        _transferred = true;
        return result;
    }

    private void ReleaseDeadline() => DisposableExtensions.DisposeAndNull(ref _deadline);

    internal void HandleFailure(Exception exception)
    {
        Exception failure = exception is OperationCanceledException && Expired ? new GethTraceDeadlineException() : exception;
        try { Dispose(); }
        catch (Exception cleanupException)
        {
            failure.Data[nameof(GethLikeBlockDeadlineTracer)] = cleanupException;
        }
        if (!ReferenceEquals(failure, exception)) throw failure;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ReleaseDeadline();
            if (!_transferred)
                foreach (GethLikeTxTrace trace in inner.BuildResult()) trace.Dispose();
        }
        finally
        {
            inner.TryDispose();
        }
    }
}

internal sealed class GethTraceDeadlineException() : Exception("execution timeout");
