// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.Tracing;

/// <summary>Finds the lowest gas limit a transaction executes successfully with, against one block context.</summary>
/// <remarks>
/// The search runs the transaction at the highest fundable gas limit first, starts the lower bound at the gas
/// that run used, tries an optimistic guess derived from its peak gas, then bisects with a midpoint skewed to
/// the low side until the bounds are within the allowed error ratio of the upper bound.
/// </remarks>
public class GasEstimator(ITransactionProcessor transactionProcessor, IReadOnlyStateProvider stateProvider)
{
    /// <summary>Error margin used if none other is specified, expressed in basis points.</summary>
    public const int DefaultErrorMargin = 150;

    /// <summary>Prefix of the error message emitted when the required gas exceeds what the sender can afford.</summary>
    public const string GasExceedsAllowanceMsgPrefix = "gas required exceeds allowance";

    /// <summary>Message emitted when the sender's balance does not exceed the transferred value.</summary>
    public const string InsufficientBalance = TxErrorMessages.InsufficientFundsForTransfer;

    /// <summary>Message emitted when the sender cannot cover the blob fee on top of the value.</summary>
    public const string InsufficientFundsForGas = TxErrorMessages.InsufficientFundsForGas;

    /// <summary>Reported when an EIP-8141 frame transaction's reservation exceeds the block or transaction gas limits.</summary>
    public const string CannotEstimateGasExceeded = "Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap";

    private const int MaxErrorMargin = 10000;
    private const double BasisPointsDivisor = 10000d;
    private static readonly string InvalidErrorMarginTooHigh = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
    private const string TransactionExecutionFails = "Transaction execution fails";
    private const string ExecutionReverted = "execution reverted";
    private const string FrameTxGasLimitOverflows = "frame transaction gas limit overflows";

    public GasEstimation Estimate(
        Transaction tx,
        in BlockExecutionContext blockContext,
        ulong errorMargin = DefaultErrorMargin,
        ulong gasCap = 0,
        FundedRunContext? fundedRun = null,
        CancellationToken token = default)
    {
        if (errorMargin >= MaxErrorMargin)
            return GasEstimation.Failure(InvalidErrorMarginTooHigh);

        BlockHeader header = blockContext.Header;
        IReleaseSpec spec = blockContext.Spec;
        tx.SenderAddress ??= Address.Zero;

        if (tx.SupportsFrames)
            return EstimateFrameTx(tx, header, spec);

        ulong hi = tx.GasLimit >= GasCostOf.Transaction ? tx.GasLimit : header.GasLimit;

        // EIP-7825 caps the gas of a single transaction until EIP-8037 lifts the cap to both gas dimensions.
        if (hi > Eip7825Constants.DefaultTxGasLimitCap && spec.IsEip7825Enabled && !spec.IsEip8037Enabled)
            hi = Eip7825Constants.DefaultTxGasLimitCap;

        UInt256 feeCap = tx.MaxFeePerGas;
        if (!feeCap.IsZero)
        {
            UInt256 available = stateProvider.GetBalance(tx.SenderAddress);
            if (tx.ValueRef >= available)
                return GasEstimation.Failure(InsufficientBalance);

            available -= tx.ValueRef;

            if (spec.IsEip4844Enabled && tx.BlobVersionedHashes is { Length: > 0 } blobHashes)
            {
                if (!BlobGasCalculator.TryCalculateBlobMaxFee(blobHashes.Length, tx.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobFee)
                    || blobFee >= available)
                    return GasEstimation.Failure(InsufficientFundsForGas);

                available -= blobFee;
            }

            UInt256 allowance = available / feeCap;
            if (allowance <= ulong.MaxValue && hi > (ulong)allowance)
                hi = (ulong)allowance;
        }

        if (gasCap != 0 && hi > gasCap)
            hi = gasCap;

        Execution execution = new(transactionProcessor, tx, blockContext, token);

        // A plain transfer runs no code and earns no refund, so one run at the base cost is exact when it passes.
        if (tx.Data.IsEmpty && tx.To is not null && !stateProvider.IsContract(tx.To))
        {
            Run transfer = execution.Run(GasCostOf.Transaction);
            if (transfer.Status == RunStatus.Succeeded)
                return GasEstimation.Success(transfer.GasUsed);
        }

        Run probe = execution.Run(hi);
        switch (probe.Status)
        {
            case RunStatus.Rejected:
                return GasEstimation.Rejected(probe.Error!, hi);
            case RunStatus.Failed when probe.Reverted:
                return GasEstimation.Revert(probe.Error!, probe.ReturnValue);
            case RunStatus.Failed when !probe.OutOfGas:
                return execution.TryDescribeFailure(hi, in probe, out string failureText)
                    ? GasEstimation.Failure(failureText)
                    : ReportFailure(execution, tx, fundedRun ?? new FundedRunContext(spec, null), probe, hi);
            case RunStatus.Failed:
            case RunStatus.FailedBelowGasLimitBounds:
                return GasEstimation.Failure($"{GasExceedsAllowanceMsgPrefix} ({hi})");
        }

        ulong lo = probe.GasUsed - 1;

        // EIP-150 withholds 1/64 of the gas at every call, so the peak gas plus a call stipend, scaled by 64/63,
        // is usually enough for the transaction to succeed.
        ulong optimistic = (probe.MaxUsedGas + GasCostOf.CallStipend) * 64 / 63;
        if (optimistic < hi)
        {
            Run run = execution.Run(optimistic);
            if (run.Status == RunStatus.Rejected)
                return GasEstimation.Rejected(run.Error!, optimistic);

            if (run.Status == RunStatus.Succeeded)
                hi = optimistic;
            else
                lo = optimistic;
        }

        double errorRatio = errorMargin / BasisPointsDivisor;
        while (lo + 1 < hi)
        {
            if (errorRatio > 0 && IsWithinErrorRatio(lo, hi, errorRatio))
                break;

            ulong mid = NextGasLimit(lo, hi);
            Run run = execution.Run(mid);
            if (run.Status == RunStatus.Rejected)
                return GasEstimation.Rejected(run.Error!, mid);

            if (run.Status == RunStatus.Succeeded)
                hi = mid;
            else
                lo = mid;
        }

        return GasEstimation.Success(hi);
    }

    /// <summary>Reports an execution failure at the highest gas limit that has no standard text, as it always was.</summary>
    /// <remarks>
    /// The run at the requested gas limit, capped by what the balance pays for at the fee cap, names the failure;
    /// when that run is below the intrinsic cost, or passes, the failure at the highest limit is reported as the
    /// processor described it.
    /// </remarks>
    private GasEstimation ReportFailure(Execution execution, Transaction tx, FundedRunContext fundedRun, in Run probe, ulong hi)
    {
        ulong fundedGasLimit = FundedGasLimit(tx, fundedRun.Spec);
        Run funded = fundedGasLimit == hi && fundedRun.MaxFeePerBlobGas is null
            ? probe
            : execution.Run(fundedGasLimit, fundedRun.MaxFeePerBlobGas);
        string processorError = probe.TracerError ?? TransactionExecutionFails;

        return funded switch
        {
            { Status: RunStatus.Succeeded } => GasEstimation.Failure(processorError),
            { Status: RunStatus.FailedBelowGasLimitBounds, RejectionType: TransactionResult.ErrorType.GasLimitBelowIntrinsicGas or TransactionResult.ErrorType.GasLimitExceedsMaxTotalCap }
                => GasEstimation.Failure(processorError),
            _ => GasEstimation.Rejected(funded.Error ?? processorError, fundedGasLimit),
        };
    }

    /// <summary>The requested gas limit, capped by what the balance left after the value and blob fee pays for.</summary>
    private ulong FundedGasLimit(Transaction tx, IReleaseSpec spec)
    {
        UInt256 feeCap = tx.CalculateFeeCap();
        if (feeCap.IsZero || UInt256.SubtractUnderflow(stateProvider.GetBalance(tx.SenderAddress!), tx.ValueRef, out UInt256 available))
            return tx.GasLimit;

        if (!BlobGasCalculator.TrySubtractBlobFee(spec, tx, ref available))
            available = UInt256.Zero;

        UInt256 allowance = available / feeCap;
        return allowance < tx.GasLimit ? (ulong)allowance : tx.GasLimit;
    }

    /// <summary>Whether the gap between the bounds is below <paramref name="errorRatio"/> of the upper bound.</summary>
    internal static bool IsWithinErrorRatio(ulong lo, ulong hi, double errorRatio) => (double)(hi - lo) / hi < errorRatio;

    /// <summary>The next gas limit to try: the midpoint, but never more than twice the lower bound.</summary>
    /// <remarks>Most transactions need little more than the gas they use, so the bisection is skewed to the low side.</remarks>
    internal static ulong NextGasLimit(ulong lo, ulong hi)
    {
        ulong mid = lo + (hi - lo) / 2;
        return mid > lo * 2 ? lo * 2 : mid;
    }

    /// <summary>The gas an EIP-8141 frame transaction reserves, or a failure when that budget is unestimable.</summary>
    /// <remarks>There is nothing to binary-search: the budget is fixed by the signed per-frame limits. The
    /// sender's balance is not gated on either, since the payer is frame-chosen rather than the sender.
    /// The reported budget is the combined reservation, but admission bounds the execution and state
    /// dimensions separately, so the combined figure is not what either limit is tested against.</remarks>
    private static GasEstimation EstimateFrameTx(Transaction tx, BlockHeader header, IReleaseSpec spec)
    {
        // The budget below is computable from an empty or oversized frame list, so a count no valid
        // transaction can carry is reported rather than priced.
        if (tx.Frames is not { Length: > 0 and <= Eip8141Constants.MaxFrames })
            return GasEstimation.Failure(FrameTxValidation.MissingFrames);

        if (!FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong maxGas)
            || !FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation))
            return GasEstimation.Failure(FrameTxGasLimitOverflows);

        // EIP-8037: each dimension gets its own block budget, and execution carries the per-tx cap on top.
        return executionReservation > Math.Min(header.GasLimit, Eip7825Constants.DefaultTxGasLimitCap) || stateReservation > header.GasLimit
            ? GasEstimation.Failure(CannotEstimateGasExceeded)
            : GasEstimation.Success(maxGas);
    }

    private enum RunStatus
    {
        Succeeded,
        Failed,
        FailedBelowGasLimitBounds,
        Rejected,
    }

    private readonly record struct Run(
        RunStatus Status,
        ulong GasUsed = 0,
        ulong MaxUsedGas = 0,
        string? Error = null,
        bool Reverted = false,
        bool OutOfGas = false,
        byte[]? ReturnValue = null,
        EvmExceptionType ExceptionType = EvmExceptionType.None,
        string? TracerError = null,
        TransactionResult.ErrorType RejectionType = TransactionResult.ErrorType.None);

    private sealed class Execution
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly Transaction _tx;
        private readonly BlockExecutionContext _blockContext;
        private readonly EstimationTracer _tracer;
        private readonly ITxTracer _cancellableTracer;
        private readonly CancellationToken _token;

        public Execution(ITransactionProcessor transactionProcessor, Transaction tx, in BlockExecutionContext blockContext, CancellationToken token)
        {
            _transactionProcessor = transactionProcessor;
            _token = token;
            _tx = tx;
            _blockContext = blockContext;
            // Only a creation can run out of gas after its frame completes, while depositing the code.
            _tracer = new EstimationTracer(tracksFrames: tx.IsContractCreation);
            _cancellableTracer = _tracer.WithCancellation(token);
            _tipAboveFeeCap = TipAboveFeeCap(tx, blockContext.Spec);
        }

        private readonly string? _tipAboveFeeCap;

        /// <summary>
        /// A priced transaction whose priority fee exceeds its fee cap is rejected before any gas is bought, whatever
        /// its gas limit; the processor checks only the fee cap against the base fee when validation is skipped.
        /// </summary>
        private static string? TipAboveFeeCap(Transaction tx, IReleaseSpec spec) =>
            spec.IsEip1559Enabled
            && !(tx.MaxFeePerGas.IsZero && tx.MaxPriorityFeePerGas.IsZero)
            && tx.MaxFeePerGas < tx.MaxPriorityFeePerGas
                ? $"{TxErrorMessages.TipAboveFeeCap}: address {tx.SenderAddress!.ToString(withEip55Checksum: true)}, maxPriorityFeePerGas: {tx.MaxPriorityFeePerGas}, maxFeePerGas: {tx.MaxFeePerGas}"
                : null;

        public bool TryDescribeFailure(ulong gasLimit, in Run run, out string text) =>
            ExecutionFailureText.TryDescribe(_transactionProcessor, CloneWithGasLimit(gasLimit), in _blockContext, run.ExceptionType, run.Error!, _token, out text);

        private Transaction CloneWithGasLimit(ulong gasLimit)
        {
            Transaction txClone = new();
            _tx.CopyTo(txClone, copyHash: false);
            txClone.GasLimit = gasLimit;
            return txClone;
        }

        public Run Run(ulong gasLimit, UInt256? maxFeePerBlobGas = null)
        {
            if (_tipAboveFeeCap is not null)
                return new Run(RunStatus.Rejected, Error: _tipAboveFeeCap);

            Transaction txClone = CloneWithGasLimit(gasLimit);
            if (maxFeePerBlobGas is not null)
                txClone.MaxFeePerBlobGas = maxFeePerBlobGas;

            _tracer.ResetRun();
            TransactionResult result;
            try
            {
                result = _transactionProcessor.CallAndRestore(txClone, in _blockContext, _cancellableTracer);
            }
            catch (InsufficientBalanceException)
            {
                result = TransactionResult.InsufficientSenderBalance;
            }

            if (!result.TransactionExecuted)
            {
                // A limit below the intrinsic cost, or above what one transaction may carry, is fixed by moving
                // the gas limit, so it narrows the search instead of ending it.
                return result.Error is TransactionResult.ErrorType.GasLimitBelowIntrinsicGas
                    or TransactionResult.ErrorType.GasLimitExceedsMaxTotalCap
                    or TransactionResult.ErrorType.BlockGasLimitExceeded
                    ? new Run(RunStatus.FailedBelowGasLimitBounds, Error: result.ErrorDescription, RejectionType: result.Error)
                    : new Run(RunStatus.Rejected, Error: result.ErrorDescription, RejectionType: result.Error);
            }

            if (result.EvmExceptionType == EvmExceptionType.None && _tracer.StatusCode == StatusCode.Success)
                return new Run(RunStatus.Succeeded, _tracer.GasSpent, _tracer.MaxUsedGas);

            bool reverted = result.EvmExceptionType == EvmExceptionType.Revert;
            return new Run(
                RunStatus.Failed,
                _tracer.GasSpent,
                _tracer.MaxUsedGas,
                result.GetErrorMessage(_tracer.Error) ?? (reverted ? ExecutionReverted : TransactionExecutionFails),
                reverted,
                result.EvmExceptionType == EvmExceptionType.OutOfGas && (!_tracer.TracksFrames || _tracer.TopFrameOutOfGas),
                _tracer.ReturnValue,
                result.EvmExceptionType,
                _tracer.Error);
        }
    }

    /// <summary>Output tracer that can also tell whether the outermost frame itself ran out of gas.</summary>
    private sealed class EstimationTracer(bool tracksFrames) : CallOutputTracer
    {
        private int _depth;
        private bool _inPrecompile;

        public bool TracksFrames => tracksFrames;
        public bool TopFrameOutOfGas { get; private set; }
        public override bool IsTracingActions => tracksFrames;

        public void ResetRun()
        {
            Reset();
            _depth = 0;
            _inPrecompile = false;
            TopFrameOutOfGas = false;
        }

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (isPrecompileCall)
                _inPrecompile = true;
            else
                _depth++;
        }

        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => ExitFrame();

        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => ExitFrame();

        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => ExitFrame();

        public override void ReportActionError(EvmExceptionType exceptionType)
        {
            if (!_inPrecompile && _depth == 1 && exceptionType == EvmExceptionType.OutOfGas)
                TopFrameOutOfGas = true;

            ExitFrame();
        }

        private void ExitFrame()
        {
            if (_inPrecompile)
                _inPrecompile = false;
            else
                _depth--;
        }
    }
}

/// <summary>How a failure without standard text is reported.</summary>
/// <param name="Spec">The spec the funded gas limit's blob fee is gated by.</param>
/// <param name="MaxFeePerBlobGas">The blob fee cap the run at the funded gas limit uses in place of the request's, if any.</param>
public readonly record struct FundedRunContext(IReleaseSpec Spec, UInt256? MaxFeePerBlobGas);

/// <summary>The outcome of a gas estimation.</summary>
/// <param name="Gas">The estimated gas limit; zero on failure.</param>
/// <param name="Error">Why estimation failed, or null on success.</param>
/// <param name="Reverted">Whether the failure is a revert of the transaction at the highest gas limit.</param>
/// <param name="RevertData">The revert payload when <paramref name="Reverted"/>.</param>
/// <param name="RejectedGasLimit">The gas limit the transaction was rejected at before execution, or null when it was not rejected.</param>
public readonly record struct GasEstimation(ulong Gas, string? Error, bool Reverted = false, byte[]? RevertData = null, ulong? RejectedGasLimit = null)
{
    public static GasEstimation Success(ulong gas) => new(gas, null);
    public static GasEstimation Failure(string error) => new(0, error);
    public static GasEstimation Revert(string error, byte[]? revertData) => new(0, error, Reverted: true, RevertData: revertData);
    public static GasEstimation Rejected(string error, ulong gasLimit) => new(0, error, RejectedGasLimit: gasLimit);
}
