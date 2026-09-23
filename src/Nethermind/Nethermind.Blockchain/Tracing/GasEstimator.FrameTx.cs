// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public partial class GasEstimator
{
    private const int MaxFrameProbes = 512;

    /// <summary>Fills omitted frame limits by replaying the complete transaction in the caller's state scope.</summary>
    public Result<TxFrame[]> EstimateFrameGas(Transaction transaction, BlockHeader header,
        bool[] fillExecution, bool[] fillState, ulong gasCap, int errorMargin, CancellationToken token)
    {
        if (errorMargin < 0 || errorMargin >= MaxErrorMargin)
            return Result<TxFrame[]>.Fail(errorMargin < 0 ? InvalidErrorMarginNegative : InvalidErrorMarginTooHigh);
        Transaction tx = new();
        transaction.CopyTo(tx, copyHash: false);
        TxFrame[] frames = (TxFrame[])transaction.Frames!.Clone();
        tx.Frames = frames;
        tx.SenderAddress ??= Address.Zero;
        tx.Nonce = stateProvider.GetNonce(tx.SenderAddress);
        IReleaseSpec spec = specProvider.GetSpec(header);
        ulong executionCap = Math.Min(gasCap, Math.Min(header.GasLimit, Eip7825Constants.DefaultTxGasLimitCap));
        ulong stateCap = Math.Min(gasCap, header.GasLimit);
        if (!FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong reservedExecution, out ulong reservedState, estimateSignatureBytes: true)
            || reservedExecution > executionCap || reservedState > stateCap)
            return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);
        ulong executionRoom = executionCap - reservedExecution;
        ulong stateRoom = stateCap - reservedState;
        // Upper probes preserve dependencies on earlier frames, including calls whose failures are caught.
        // Only the final limits must fit the aggregate reservation caps.
        for (int i = 0; i < frames.Length; i++)
            frames[i] = WithGas(frames[i], fillExecution[i] ? executionRoom : frames[i].ExecutionGasLimit,
                fillState[i] ? stateRoom : frames[i].StateGasLimit);

        UInt256 gasPrice = tx.GasPrice;
        UInt256 feeCap = tx.DecodedMaxFeePerGas;
        tx.GasPrice = 0;
        tx.DecodedMaxFeePerGas = 0;
        int probes = 0;
        FrameEstimateTracer tracer = Probe(realFees: false, out string? error);
        if (error is not null) return Result<TxFrame[]>.Fail(error);

        for (int i = 0; i < frames.Length; i++)
        {
            TxFrameReceipt receipt = tracer.Receipts![i];
            if (fillExecution[i]) Minimize(i, true, receipt.ExecutionGasUsed);
            if (fillState[i]) Minimize(i, false, receipt.StateGasUsed);
        }

        if (!FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out reservedExecution, out reservedState, estimateSignatureBytes: true)
            || reservedExecution > executionCap || reservedState > stateCap
            || !FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong totalGas, estimateSignatureBytes: true)
            || totalGas > gasCap)
            return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);

        tx.IntrinsicGasMemo = null;
        tx.GasPrice = gasPrice;
        tx.DecodedMaxFeePerGas = feeCap;
        Probe(realFees: true, out error);
        return error is null ? frames : Result<TxFrame[]>.Fail(error);

        FrameEstimateTracer Probe(bool realFees, out string? error)
        {
            token.ThrowIfCancellationRequested();
            probes++;
            Transaction probe = new();
            tx.CopyTo(probe, copyHash: false);
            probe.GasLimit = FrameTxValidation.TotalGasLimit(frames);
            BlockHeader probeHeader = header.Clone();
            probeHeader.GasUsed = 0;
            if (!realFees) probeHeader.BaseFeePerGas = 0;
            FrameEstimateTracer output = new();
            transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(probeHeader, spec));
            TransactionResult result = transactionProcessor.CallAndRestore(probe, output.WithCancellation(token));
            error = result.GetErrorMessage(output.Error);
            if (error is null && output.FailedFrame is { } index)
                error = $"frame {index} failed: {output.FrameError}";
            if (error is null && output.Receipts is null) error = "frame transaction simulation failed";
            return output;
        }

        void Minimize(int index, bool execution, ulong used)
        {
            if (probes >= MaxFrameProbes - 1) return;
            TxFrame frame = frames[index];
            ulong high = execution ? frame.ExecutionGasLimit : frame.StateGasLimit;
            ulong low = Math.Min(used, high);
            ulong candidate = Math.Min(high, execution ? (ulong)Math.Ceiling(low * OptimisticMultiplier) : low);
            for (int attempt = 0; attempt < 8 && probes < MaxFrameProbes - 1; attempt++)
            {
                frames[index] = WithGas(frame, execution ? candidate : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : candidate);
                Probe(realFees: false, out string? failure);
                if (failure is null) high = candidate;
                else low = candidate + 1;
                if (low >= high || high - low <= high * (ulong)errorMargin / 10000) break;
                candidate = low + (high - low) / 2;
            }
            frames[index] = WithGas(frame, execution ? high : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : high);
        }
    }

    private static TxFrame WithGas(TxFrame frame, ulong execution, ulong state) =>
        new(frame.Mode, frame.Flags, frame.Target, execution, state, frame.Value, frame.Data);

    private sealed class FrameEstimateTracer : CallOutputTracer, IFrameTxReceiptTracer
    {
        public TxFrameReceipt[]? Receipts { get; private set; }
        public int? FailedFrame { get; private set; }
        public EvmExceptionType? FrameError { get; private set; }
        public void ReportFrameEnd(int frameIndex, EvmExceptionType? error)
        {
            if (error is not null && FailedFrame is null)
            {
                FailedFrame = frameIndex;
                FrameError = error;
            }
        }
        public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => Receipts = frameReceipts;
    }
}
