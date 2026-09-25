// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

/// <summary>Collects call traces with log indices that include preceding transactions in the block.</summary>
public sealed class GethLikeBlockCallTracer(Hash256? txHash, NativeTracerFactory txTracerFactory) : IBlockTracer<GethLikeTxTrace>, IDisposable
{
    private readonly IBlockTracer<GethLikeTxTrace> _inner = new GethLikeBlockNativeTracer(txHash, txTracerFactory);
    private readonly LogCounter _counter = new();

    /// <inheritdoc/>
    public bool IsTracingRewards => _inner.IsTracingRewards;

    /// <inheritdoc/>
    public void StartNewBlockTrace(Block block)
    {
        _counter.Count = 0;
        _inner.StartNewBlockTrace(block);
    }

    /// <inheritdoc/>
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        ITxTracer tracer = _inner.StartNewTxTrace(tx);
        if (tracer is NativeCallTracer callTracer)
            callTracer.LogIndexOffset = _counter.Count;
        return tracer == NullTxTracer.Instance ? _counter : new CompositeTxTracer(tracer, _counter);
    }

    /// <inheritdoc/>
    public void EndTxTrace() => _inner.EndTxTrace();

    /// <inheritdoc/>
    public void EndBlockTrace() => _inner.EndBlockTrace();

    /// <inheritdoc/>
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _inner.ReportReward(author, rewardType, rewardValue);

    /// <inheritdoc/>
    public IReadOnlyCollection<GethLikeTxTrace> BuildResult() => _inner.BuildResult();

    /// <inheritdoc/>
    public void Dispose() => _inner.TryDispose();

    private sealed class LogCounter : TxTracer
    {
        public ulong Count { get; set; }
        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            Count += (ulong)logs.Length;
    }
}
