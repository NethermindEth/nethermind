// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Block-level tracer that wraps an inner <see cref="IBlockTracer"/> and adds
/// ZK gas metering via <see cref="ZkGasTxTracer"/> for every transaction.
/// A fresh <see cref="ZkGasMeter"/> is created for each block, sized according
/// to the active network's Unzen block ZK gas limit.
/// </summary>
public sealed class ZkGasBlockTracer(IBlockTracer inner, ZkGasMeterHolder? holder = null, ulong blockZkGasLimit = ZkGasSchedule.BlockZkGasLimit) : IBlockTracer
{
    private readonly IBlockTracer _inner = inner;
    private readonly ZkGasMeterHolder? _holder = holder;
    private readonly ulong _blockZkGasLimit = blockZkGasLimit;
    private ZkGasMeter _meter = new(blockZkGasLimit);

    /// <summary>The ZK gas meter for the current block.</summary>
    public ZkGasMeter Meter => _meter;

    /// <inheritdoc />
    public bool IsTracingRewards => _inner.IsTracingRewards;

    /// <inheritdoc />
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        => _inner.ReportReward(author, rewardType, rewardValue);

    /// <summary>
    /// Resets the meter for the new block, publishes it to the holder, and forwards to the inner tracer.
    /// </summary>
    public void StartNewBlockTrace(Block block)
    {
        _meter = new ZkGasMeter(_blockZkGasLimit);
        if (_holder is not null) _holder.Meter = _meter;
        _inner.StartNewBlockTrace(block);
    }

    /// <summary>
    /// Resets in-flight transaction gas, obtains the inner per-tx tracer,
    /// creates a <see cref="ZkGasTxTracer"/>, and returns a composite of both.
    /// </summary>
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _meter.ResetTransaction();
        ITxTracer innerTxTracer = _inner.StartNewTxTrace(tx);
        ZkGasTxTracer zkTracer = new(_meter);
        return new CompositeTxTracer(innerTxTracer, zkTracer);
    }

    /// <summary>
    /// Commits the current transaction's ZK gas and forwards to the inner tracer.
    /// </summary>
    public void EndTxTrace()
    {
        _meter.CommitTransaction();
        _inner.EndTxTrace();
    }

    /// <inheritdoc />
    public void EndBlockTrace() => _inner.EndBlockTrace();
}
