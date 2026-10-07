// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

internal sealed class RecoveringJavaScriptBlockTracer(Func<IBlockTracer<GethLikeTxTrace>> createTracer, Hash256? txHash) : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    private IBlockTracer<GethLikeTxTrace>? _inner;
    private string? _constructionError;
    private string? _transactionError;
    private Hash256? _transactionHash;
    private readonly List<GethLikeTxTrace?> _outcomes = [];
    private GethLikeTxTrace[]? _result;

    public bool IsTracingRewards => false;

    public void StartNewBlockTrace(Block block)
    {
        try
        {
            _inner = createTracer();
        }
        catch (Exception exception) when (JavaScriptTraceFailure.IsRecoverable(exception))
        {
            _constructionError = exception.Message;
        }
        _inner?.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? transaction)
    {
        _transactionHash = txHash is null || transaction?.Hash == txHash ? transaction?.Hash : null;
        _transactionError = _constructionError;
        if (_transactionHash is null || _transactionError is not null) return NullTxTracer.Instance;
        try
        {
            return _inner!.StartNewTxTrace(transaction);
        }
        catch (Exception exception) when (JavaScriptTraceFailure.IsRecoverable(exception))
        {
            _transactionError = exception.Message;
            // The transaction still executes without tracing, preserving the next transaction's prestate.
            return NullTxTracer.Instance;
        }
    }

    public void EndTxTrace()
    {
        if (_transactionHash is null) return;
        if (_transactionError is null)
        {
            try
            {
                _inner!.EndTxTrace();
            }
            catch (Exception exception) when (JavaScriptTraceFailure.IsRecoverable(exception))
            {
                _transactionError = exception.Message;
            }
        }
        _outcomes.Add(_transactionError is null ? null : new GethLikeTxTrace { TxHash = _transactionHash, TraceError = _transactionError });
        _transactionHash = null;
    }

    public void EndBlockTrace() => _inner?.EndBlockTrace();
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        if (_result is not null) return _result;
        GethLikeTxTrace[] result = new GethLikeTxTrace[_outcomes.Count];
        using IEnumerator<GethLikeTxTrace> successes = (_inner?.BuildResult() ?? Array.Empty<GethLikeTxTrace>()).GetEnumerator();
        for (int i = 0; i < result.Length; i++)
        {
            if (_outcomes[i] is { } error) result[i] = error;
            else
            {
                if (!successes.MoveNext()) throw new InvalidOperationException("Missing JavaScript transaction trace.");
                result[i] = successes.Current;
            }
        }
        return _result = result;
    }

    public void Dispose()
    {
        try
        {
            if (_result is null && _inner is not null)
                foreach (GethLikeTxTrace trace in _inner.BuildResult()) trace.Dispose();
        }
        finally
        {
            (_inner as IDisposable)?.Dispose();
        }
    }
}
