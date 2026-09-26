// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
    /// <remarks>Every probe keeps its omitted execution and state limits within the rooms the final limits must fit,
    /// and its frame limits plus signature verification work within <paramref name="gasCap"/>, the work bound
    /// <c>FrameTransactionForRpc.ToTransaction</c> enforces on explicit limits. Frames are minimised in order: the
    /// frame being searched takes what the others leave, while each later frame holds a reservation measured by a
    /// first probe that splits the rooms evenly.</remarks>
    /// <param name="context">The block the estimate runs in; each probe runs in a copy of its header.</param>
    /// <param name="executionReverted">Whether the failure is a frame of an otherwise valid transaction reverting.</param>
    public Result<TxFrame[]> EstimateFrameGas(Transaction transaction, BlockExecutionContext context,
        bool[] fillExecution, bool[] fillState, ulong gasCap, int errorMargin, CancellationToken token, out bool executionReverted)
    {
        executionReverted = false;
        if (errorMargin < 0 || errorMargin >= MaxErrorMargin)
            return Result<TxFrame[]>.Fail(errorMargin < 0 ? InvalidErrorMarginNegative : InvalidErrorMarginTooHigh);
        Transaction tx = new();
        transaction.CopyTo(tx, copyHash: false);
        TxFrame[] frames = (TxFrame[])transaction.Frames!.Clone();
        tx.Frames = frames;
        tx.SenderAddress ??= Address.Zero;
        tx.Nonce = stateProvider.GetNonce(tx.SenderAddress);
        BlockHeader header = context.Header;
        IReleaseSpec spec = context.Spec;
        UInt256 blobBaseFee = new(context.BlobBaseFee.Bytes, isBigEndian: true);
        ulong executionCap = Math.Min(gasCap, Math.Min(header.GasLimit, Eip7825Constants.DefaultTxGasLimitCap));
        ulong stateCap = Math.Min(gasCap, header.GasLimit);
        if (!FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong reservedExecution, out ulong reservedState, estimateSignatureBytes: true)
            || reservedExecution > executionCap || reservedState > stateCap)
            return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);

        ulong fixedGas = FrameTxValidation.SignatureVerificationWorkGas(tx);
        int executionCount = 0;
        int stateCount = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            fixedGas = fixedGas.SaturatingAdd(fillExecution[i] ? 0 : frames[i].ExecutionGasLimit).SaturatingAdd(fillState[i] ? 0 : frames[i].StateGasLimit);
            if (fillExecution[i]) executionCount++;
            if (fillState[i]) stateCount++;
        }
        if (fixedGas > gasCap) return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);
        ulong fillBudget = gasCap - fixedGas;
        ulong executionPool = executionCount == 0 ? 0 : Math.Min(executionCap - reservedExecution, fillBudget);
        ulong statePool = stateCount == 0 ? 0 : Math.Min(stateCap - reservedState, fillBudget);
        // When the gas cap cannot cover both rooms, neither dimension is squeezed below half of the budget.
        bool capLimited = (executionCount > 0 && executionPool < executionCap - reservedExecution)
            || (stateCount > 0 && statePool < stateCap - reservedState);
        if (executionPool + statePool > fillBudget)
        {
            capLimited = true;
            ulong half = fillBudget / 2;
            if (executionPool <= half) statePool = fillBudget - executionPool;
            else if (statePool <= half) executionPool = fillBudget - statePool;
            else (executionPool, statePool) = (half, fillBudget - half);
        }

        ulong[] executionReserve = new ulong[frames.Length];
        ulong[] stateReserve = new ulong[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            executionReserve[i] = fillExecution[i] ? executionPool / (ulong)executionCount : frames[i].ExecutionGasLimit;
            stateReserve[i] = fillState[i] ? statePool / (ulong)stateCount : frames[i].StateGasLimit;
            frames[i] = WithGas(frames[i], executionReserve[i], stateReserve[i]);
        }

        UInt256 gasPrice = tx.GasPrice;
        UInt256 feeCap = tx.DecodedMaxFeePerGas;
        tx.GasPrice = 0;
        tx.DecodedMaxFeePerGas = 0;
        int probes = 0;
        bool lastProbeReverted = false;
        TxFrameReceipt[]? receipts = null;
        int? reservationFailure = null;

        // Later frames keep a margin over what they used on the even split, so their reservation absorbs usage that
        // shifts as earlier frames are minimised.
        FrameEstimateTracer split = Probe(realFees: false, out _);
        for (int i = 0; split.Receipts is not null && i < frames.Length; i++)
        {
            if (split.Receipts[i].Status != TxFrameReceipt.StatusSuccess) continue;
            if (fillExecution[i]) executionReserve[i] = Math.Min(executionReserve[i], split.Receipts[i].ExecutionGasUsed.SaturatingAdd(split.Receipts[i].ExecutionGasUsed));
            if (fillState[i]) stateReserve[i] = Math.Min(stateReserve[i], split.Receipts[i].StateGasUsed.SaturatingAdd(split.Receipts[i].StateGasUsed));
        }

        for (int i = 0; i < frames.Length; i++)
        {
            if (!fillExecution[i] && !fillState[i]) continue;
            RaiseToUpperLimits(i);
            reservationFailure = null;
            if (probes < MaxFrameProbes - 1 && !TryProbe(i, atUpperLimits: true, out string? error))
            {
                executionReverted = lastProbeReverted;
                return Result<TxFrame[]>.Fail(error!);
            }
            if (fillState[i]) Minimize(i, false, receipts?[i]);
            if (fillExecution[i]) Minimize(i, true, receipts?[i]);
        }

        if (!FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out reservedExecution, out reservedState, estimateSignatureBytes: true)
            || reservedExecution > executionCap || reservedState > stateCap
            || !FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong totalGas, estimateSignatureBytes: true)
            || totalGas > gasCap)
            return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);

        tx.IntrinsicGasMemo = null;
        tx.GasPrice = gasPrice;
        tx.DecodedMaxFeePerGas = feeCap;
        Probe(realFees: true, out string? finalError);
        executionReverted = finalError is not null && lastProbeReverted;
        return finalError is null ? frames : Result<TxFrame[]>.Fail(finalError);

        // Frames before index hold their final limits and later frames their reservations; index takes the rest.
        void RaiseToUpperLimits(int index)
        {
            ulong execution = executionPool;
            ulong state = statePool;
            for (int i = 0; i < frames.Length; i++)
            {
                if (i == index) continue;
                ulong otherExecution = i < index ? frames[i].ExecutionGasLimit : executionReserve[i];
                ulong otherState = i < index ? frames[i].StateGasLimit : stateReserve[i];
                if (i > index) frames[i] = WithGas(frames[i], otherExecution, otherState);
                if (fillExecution[i]) execution = Deduct(execution, otherExecution);
                if (fillState[i]) state = Deduct(state, otherState);
            }
            frames[index] = WithGas(frames[index], fillExecution[index] ? execution : frames[index].ExecutionGasLimit,
                fillState[index] ? state : frames[index].StateGasLimit);
        }

        // Frames up to and including index must succeed. At its upper limits a later frame may run out of gas, since it
        // holds only a reservation until its own turn; a smaller limit must not make any other later frame fail.
        bool TryProbe(int index, bool atUpperLimits, out string? error)
        {
            FrameEstimateTracer output = Probe(realFees: false, out string? failure);
            bool reservationBound = output.FailedFrame > index && output.FrameError == EvmExceptionType.OutOfGas
                && (atUpperLimits || output.FailedFrame == reservationFailure);
            if (failure is null || reservationBound)
            {
                if (atUpperLimits) reservationFailure = output.FailedFrame;
                receipts = output.Receipts;
                error = null;
                return true;
            }

            error = capLimited && output.FailedFrame == index && output.FrameError == EvmExceptionType.OutOfGas
                ? CannotEstimateGasExceeded
                : failure;
            return false;
        }

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
            transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(probeHeader, spec, blobBaseFee));
            TransactionResult result = transactionProcessor.CallAndRestore(probe, output.WithCancellation(token));
            error = result.GetErrorMessage(output.Error);
            lastProbeReverted = result.TransactionExecuted && output.FrameError == EvmExceptionType.Revert;
            if (error is null && output.FailedFrame is { } index)
                error = $"frame {index} failed: {output.FrameError}";
            if (error is null && output.Receipts is null) error = "frame transaction simulation failed";
            return output;
        }

        // Seeded from the frame's measured use; without one, the first accepted probe supplies it.
        void Minimize(int index, bool execution, TxFrameReceipt? measured)
        {
            TxFrame frame = frames[index];
            ulong high = execution ? frame.ExecutionGasLimit : frame.StateGasLimit;
            ulong low = measured is null ? 0 : Math.Min(Used(measured, execution), high);
            ulong candidate = measured is null ? high / 2 : Optimistic(low, high, execution);
            // Out of probes: take the optimistic limit unverified; the final probe checks the whole assignment.
            if (probes >= MaxFrameProbes - 1) high = candidate;
            for (int attempt = 0; attempt < 8 && probes < MaxFrameProbes - 1; attempt++)
            {
                frames[index] = WithGas(frame, execution ? candidate : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : candidate);
                if (TryProbe(index, atUpperLimits: false, out _))
                {
                    high = candidate;
                    if (measured is null && receipts is not null)
                    {
                        measured = receipts[index];
                        low = Math.Max(low, Math.Min(Used(measured, execution), high));
                        candidate = Optimistic(low, high, execution);
                        if (candidate < high) continue;
                    }
                }
                else low = candidate + 1;
                if (low >= high || high - low <= high * (ulong)errorMargin / 10000) break;
                candidate = low + (high - low) / 2;
            }
            frames[index] = WithGas(frame, execution ? high : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : high);
        }
    }

    private static ulong Used(TxFrameReceipt receipt, bool execution) => execution ? receipt.ExecutionGasUsed : receipt.StateGasUsed;

    private static ulong Optimistic(ulong used, ulong high, bool execution) =>
        Math.Min(high, execution ? (ulong)Math.Ceiling(used * OptimisticMultiplier) : used);

    private static ulong Deduct(ulong left, ulong amount) => left > amount ? left - amount : 0;

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
