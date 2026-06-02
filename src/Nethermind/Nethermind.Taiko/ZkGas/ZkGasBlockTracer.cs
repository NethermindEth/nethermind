// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
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
public sealed class ZkGasBlockTracer : IBlockTracer
{
    private readonly IBlockTracer _inner;
    private readonly ZkGasMeter _meter;

    public ZkGasBlockTracer(IBlockTracer inner, ZkGasMeterHolder? holder = null,
        ulong blockZkGasLimit = ZkGasSchedule.BlockZkGasLimit,
        ulong txIntrinsicZkGas = ZkGasSchedule.TxIntrinsicZkGas,
        ReadOnlyMemory<ushort> opcodeMultipliers = default,
        FrozenDictionary<AddressAsKey, ushort>? precompileMultipliers = null)
    {
        _inner = inner;
        _meter = new ZkGasMeter(blockZkGasLimit, txIntrinsicZkGas, opcodeMultipliers, precompileMultipliers);
        // Publish once: the meter reference is stable across blocks (we Reset it in
        // StartNewBlockTrace rather than reallocating), so the holder never needs
        // re-pointing for the lifetime of this tracer.
        if (holder is not null) holder.Meter = _meter;
    }

    /// <summary>The ZK gas meter for the current block.</summary>
    public ZkGasMeter Meter => _meter;

    /// <inheritdoc />
    public bool IsTracingRewards => _inner.IsTracingRewards;

    /// <inheritdoc />
    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        => _inner.ReportReward(author, rewardType, rewardValue);

    /// <summary>
    /// Resets the meter's per-block accounting and forwards to the inner tracer. The
    /// meter instance itself is reused — see <see cref="ZkGasMeter.ResetBlock"/>.
    /// </summary>
    public void StartNewBlockTrace(Block block)
    {
        _meter.ResetBlock();
        _inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _meter.ResetTransaction();
        _ = _meter.ChargeTxIntrinsic();
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
