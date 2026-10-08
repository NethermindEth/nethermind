// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

#pragma warning disable NETH003 // Build variant: excluded from the zkEVM build, which does no tracing
internal sealed class GethLikeBlockCallDeadlineTracer : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    private readonly GethTraceDeadline _deadline;
    private readonly IBlockTracer<GethLikeTxTrace> _inner;
    private readonly GethTraceOptions _options;
    private bool _targetStarted;
    private bool _targetEnded;
    private bool _disposed;

    internal GethLikeBlockCallDeadlineTracer(GethTraceOptions options, CancellationToken external,
        Func<GethTraceOptions, IBlockTracer<GethLikeTxTrace>> factory, TimeProvider? clock = null)
    {
        _options = options;
        _deadline = new(external, clock);
        try { _inner = factory(options with { ExecutionCancellation = new GethTraceCancellation { Token = _deadline.Token } }); }
        catch { _deadline.Dispose(); throw; }
    }
    internal CancellationToken Token => _deadline.Token;
    internal bool Expired => _deadline.Expired;
    public bool IsTracingRewards => _inner.IsTracingRewards;
    public void StartNewBlockTrace(Block block) => _inner.StartNewBlockTrace(block);
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => _inner.ReportReward(author, rewardType, rewardValue);
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        ITxTracer tracer = _inner.StartNewTxTrace(tx);
        if (tx?.Hash == _options.TxHash)
        {
            try
            {
                // All native/JS/mux constructors and setup callbacks precede Go duration validation.
                _deadline.Start(_options.Timeout ?? TimeSpan.FromSeconds(5));
                _targetStarted = true;
            }
            catch { tracer.Dispose(); throw; }
        }
        return tracer;
    }
    public void EndTxTrace()
    {
        _inner.EndTxTrace();
        if (_targetStarted) _targetEnded = true;
    }
    public void EndBlockTrace() => _inner.EndBlockTrace();
    public IReadOnlyCollection<GethLikeTxTrace> BuildResult()
    {
        if (Expired && !IgnoresStop(_options)) throw new TimeoutException("execution timeout");
        IReadOnlyCollection<GethLikeTxTrace> results = _inner.BuildResult();
        return results;
    }
    internal IReadOnlyCollection<GethLikeTxTrace> CompleteExpired(Exception exception)
    {
        if (!IgnoresStop(_options)) throw new TimeoutException("execution timeout", exception);
        if (_targetStarted && !_targetEnded) EndTxTrace();
        return BuildResult();
    }
    private static bool IgnoresStop(GethTraceOptions options)
    {
        if (options.Tracer == "noopTracer") return true;
        if (options.Tracer != GethLikeBlockMuxTracer.TracerName) return false;
        foreach ((string name, JsonElement config) in GethLikeBlockMuxTracer.ParseConfig(options.TracerConfig))
            if (!IgnoresStop(options with { Tracer = name, TracerConfig = config })) return false;
        return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.TryDispose(); }
        finally { _deadline.Dispose(); }
    }
}
