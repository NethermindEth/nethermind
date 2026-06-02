// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// Block tracer that wraps an inner <see cref="IBlockTracer{TTrace}"/> and fires
/// <see cref="OnBlockTraceComplete"/> as soon as the inner tracer's
/// <see cref="IBlockTracer.EndBlockTrace"/> returns. This is the seam through which
/// <see cref="SimulateBridgeHelper"/> emits one block's
/// <see cref="Proxy.Models.Simulate.SimulateBlockResult{TTrace}"/> JSON to the
/// response writer before the next block starts processing.
/// <para>
/// Rationale: <c>SimulateBlockTracer.ReapplyBlockHash</c> mutates the logs of the
/// per-tx call results <i>after</i> <c>BlockProcessor.ProcessOne</c> returns with
/// the canonical post-processing block hash. Streaming individual tx results before
/// block-end would emit incorrect (zero) <c>blockHash</c> fields in <c>logs[*]</c>.
/// The streaming seam therefore lives at the block boundary — once a block is sealed
/// and re-hashed, its complete per-tx list is emitted atomically.
/// </para>
/// </summary>
public sealed class StreamingSimulateBlockTracer<TTrace> : IBlockTracer<TTrace>
{
    private readonly IBlockTracer<TTrace> _inner;
    private bool _blockInFlight;

    public StreamingSimulateBlockTracer(IBlockTracer<TTrace> inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// Fired from <see cref="EndBlockTrace"/>. The handler should serialize the
    /// completed block to the response writer and not retain a reference to the
    /// underlying <see cref="IReadOnlyCollection{TTrace}"/> after returning —
    /// <see cref="StartNewBlockTrace"/> resets the inner tracer's storage between blocks.
    /// </summary>
    public Action<IReadOnlyCollection<TTrace>>? OnBlockTraceComplete { get; set; }

    public bool BlockInFlight => _blockInFlight;

    public bool IsTracingRewards => _inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        => _inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        _blockInFlight = true;
        _inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => _inner.StartNewTxTrace(tx);

    public void EndTxTrace() => _inner.EndTxTrace();

    public void EndBlockTrace()
    {
        _inner.EndBlockTrace();
        _blockInFlight = false;
        OnBlockTraceComplete?.Invoke(_inner.BuildResult());
    }

    /// <summary>
    /// In streaming mode the per-block traces are consumed inline by
    /// <see cref="OnBlockTraceComplete"/>; callers that ask for a cross-block result get an
    /// empty collection. Mirrors the deliberately-empty enumerator on the JSON-RPC
    /// streaming result types.
    /// </summary>
    public IReadOnlyCollection<TTrace> BuildResult() => Array.Empty<TTrace>();

    /// <summary>Exposed for callers that need the inner tracer's recognition for
    /// e.g. <c>tracer is SimulateBlockTracer</c> checks (see <c>SimulateBridgeHelper</c>).</summary>
    public IBlockTracer<TTrace> Inner => _inner;
}
