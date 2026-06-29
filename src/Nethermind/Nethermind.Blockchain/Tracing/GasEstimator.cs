// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing;

public class GasEstimator(
    ITransactionProcessor transactionProcessor,
    IReadOnlyStateProvider stateProvider,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig)
{
    /// <summary>Error margin used if none other is specified, expressed in basis points.</summary>
    public const int DefaultErrorMargin = 150;

    /// <summary>Prefix of the error message emitted when the required gas exceeds what the sender can afford.</summary>
    public const string GasExceedsAllowanceMsgPrefix = "gas required exceeds allowance";

    /// <summary>Message emitted when the sender has insufficient balance.</summary>
    public const string InsufficientBalance = TxErrorMessages.InsufficientFundsForTransfer;

    /// <summary>Message emitted when the sender cannot cover gas * price + value.</summary>
    public const string InsufficientFundsForGas = "insufficient funds for gas * price + value";

    private const int MaxErrorMargin = 10000;
    private const double BasisPointsDivisor = 10000d;

    // EIP-150: each CALL site reserves 1/64 for the caller, so the optimistic guess must use 64/63, not the search-stop margin.
    private const double OptimisticMultiplier = 64d / 63d;

    private const string InvalidErrorMarginNegative = "Invalid error margin, cannot be negative.";
    private static readonly string InvalidErrorMarginTooHigh = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
    private const string GasEstimationOutOfGas = "Gas estimation failed due to out of gas";
    private const string TransactionExecutionFails = "Transaction execution fails";
    private const string CannotEstimateGasExceeded = "Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap";
    private const string ExecutionReverted = "execution reverted";

    public ulong Estimate(
        Transaction tx,
        BlockHeader header,
        EstimateGasTracer gasTracer,
        out string? err,
        ulong errorMargin = DefaultErrorMargin,
        CancellationToken token = default)
    {
        EstimationResult result = EstimateInternal(tx, header, gasTracer, errorMargin, token);
        err = result.Error;
        return result.GasEstimate;
    }

    private EstimationResult EstimateInternal(
        Transaction tx,
        BlockHeader header,
        EstimateGasTracer gasTracer,
        ulong errorMargin,
        CancellationToken token)
    {
        if (ValidateErrorMargin(errorMargin) is { } validationError)
            return validationError;

        IReleaseSpec spec = specProvider.GetSpec(header.Number + 1, header.Timestamp + blocksConfig.SecondsPerSlot);
        tx.SenderAddress ??= Address.Zero;

        UInt256 senderBalance = stateProvider.GetBalance(tx.SenderAddress!);

        if (CheckFunds(tx, spec, gasTracer, senderBalance, out UInt256 available) is { } fundsResult)
            return fundsResult;

        ulong intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec, header.GasLimit).MinimalGas;
        ulong leftBound = Math.Max(gasTracer.GasSpent.SaturatingSub(1), intrinsicGas.SaturatingSub(1));
        ulong rightBound = Math.Min(
            tx.GasLimit != 0 && tx.GasLimit >= intrinsicGas ? tx.GasLimit : header.GasLimit,
            spec.GetTxGasLimitCap());

        if (leftBound > rightBound)
            return EstimationResult.Failure(CannotEstimateGasExceeded);

        UInt256 feeCap = tx.CalculateFeeCap();
        EstimationBounds bounds = CapByAllowance(new EstimationBounds(leftBound, rightBound, intrinsicGas), available, feeCap);

        return BinarySearchEstimate(tx, header, spec, gasTracer, bounds, errorMargin, token);
    }

    private static EstimationResult? ValidateErrorMargin(ulong errorMargin) =>
        errorMargin switch
        {
            >= MaxErrorMargin => EstimationResult.Failure(InvalidErrorMarginTooHigh),
            _ => null
        };

    // Returns null if funds are sufficient (estimation continues), or a terminal result to return immediately.
    // On success, `available` holds the sender's balance after deducting value and blob fees.
    private static EstimationResult? CheckFunds(Transaction tx, IReleaseSpec spec, EstimateGasTracer gasTracer, UInt256 senderBalance, out UInt256 available)
    {
        available = UInt256.Zero;

        if (senderBalance < tx.ValueRef)
        {
            ulong additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, spec);
            return additionalGas > 0
                ? EstimationResult.Success(additionalGas)
                : EstimationResult.Failure(GetError(gasTracer, InsufficientBalance));
        }

        available = senderBalance - tx.ValueRef;

        if (!BlobGasCalculator.TrySubtractBlobFee(spec, tx, ref available))
            return EstimationResult.Failure(GetError(gasTracer, InsufficientFundsForGas));

        return null;
    }

    private static EstimationBounds CapByAllowance(EstimationBounds bounds, UInt256 available, UInt256 feeCap = default)
    {
        if (feeCap == UInt256.Zero)
            return bounds;

        ulong allowance = (ulong)UInt256.Min(available / feeCap, (UInt256)ulong.MaxValue);
        return bounds with { RightBound = Math.Min(bounds.RightBound, allowance) };
    }

    private EstimationResult BinarySearchEstimate(
        Transaction tx, BlockHeader header, IReleaseSpec spec, EstimateGasTracer gasTracer,
        EstimationBounds bounds, ulong errorMargin, CancellationToken token)
    {
        // Short-circuit: simple ETH transfers need exactly the intrinsic gas.
        if (IsSimpleTransfer(tx) && TryExecute(tx, header, spec, bounds.IntrinsicGas, gasTracer, token, out _))
            return EstimationResult.Success(bounds.IntrinsicGas);

        // Execute at maximum gas first (Geth parity): gas-related failure → allowance error; other → surface directly.
        if (!TryExecute(tx, header, spec, bounds.RightBound, gasTracer, token, out bool isGasRelatedFailure))
        {
            string error = (gasTracer.OutOfGas || isGasRelatedFailure)
                ? $"{GasExceedsAllowanceMsgPrefix} ({bounds.RightBound})"
                : GetError(gasTracer);
            return EstimationResult.Failure(error);
        }

        double marginMultiplier = errorMargin == 0 ? 1d : errorMargin / BasisPointsDivisor + 1d;
        ulong cap = bounds.RightBound;
        (ulong leftBound, ulong rightBound) = TryOptimisticEstimate(tx, header, spec, gasTracer, bounds, OptimisticMultiplier, token);

        // Narrow bounds until within the error margin (Geth approach).
        while (ShouldContinueSearch(leftBound, rightBound, marginMultiplier - 1d))
        {
            ulong mid = leftBound + (rightBound - leftBound) / 2;
            if (TryExecute(tx, header, spec, mid, gasTracer, token, out _))
                rightBound = mid;
            else
                leftBound = mid;
        }

        if (rightBound == cap && !TryExecute(tx, header, spec, rightBound, gasTracer, token, out _))
            return EstimationResult.Failure(GetError(gasTracer));

        return EstimationResult.Success(rightBound);
    }

    private (ulong Left, ulong Right) TryOptimisticEstimate(
        Transaction tx, BlockHeader header, IReleaseSpec spec, EstimateGasTracer gasTracer,
        EstimationBounds bounds, double optimisticMultiplier, CancellationToken token)
    {
        ulong leftBound = bounds.LeftBound;
        ulong rightBound = bounds.RightBound;

        // Optimistic first guess (Geth approach): reduces binary search iterations in most cases.
        ulong optimistic = (ulong)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * optimisticMultiplier);
        if (optimistic > leftBound && optimistic < rightBound)
        {
            if (TryExecute(tx, header, spec, optimistic, gasTracer, token, out _))
                rightBound = optimistic;
            else
                leftBound = optimistic;
        }

        return (leftBound, rightBound);
    }

    private bool TryExecute(Transaction transaction, BlockHeader header, IReleaseSpec spec, ulong gasLimit,
                             EstimateGasTracer gasTracer, CancellationToken token, out bool isGasRelatedFailure)
    {
        Transaction txClone = new();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        TransactionResult callResult = transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        if (IsGasRelatedFailure(callResult))
        {
            isGasRelatedFailure = true;
            return false;
        }

        isGasRelatedFailure = false;
        return callResult.TransactionExecuted && gasTracer.StatusCode == StatusCode.Success &&
               !gasTracer.OutOfGas && !gasTracer.TopLevelRevert;
    }

    private static bool IsGasRelatedFailure(TransactionResult result) =>
        result.Error is TransactionResult.ErrorType.GasLimitBelowIntrinsicGas
            or TransactionResult.ErrorType.GasLimitBelowFloorGas
            or TransactionResult.ErrorType.BlockGasLimitExceeded;

    private static bool ShouldContinueSearch(ulong leftBound, ulong rightBound, double threshold) =>
        (rightBound - leftBound) / (double)leftBound > threshold && leftBound + 1 < rightBound;

    private static bool IsSimpleTransfer(Transaction tx) =>
        tx.To is not null && tx.Data.IsEmpty && !tx.HasAuthorizationList;

    private static string GetError(EstimateGasTracer gasTracer, string defaultError = TransactionExecutionFails) =>
        gasTracer switch
        {
            { TopLevelRevert: true } => GetRevertError(gasTracer),
            { OutOfGas: true } => GasEstimationOutOfGas,
            { StatusCode: StatusCode.Failure } => gasTracer.Error ?? defaultError,
            _ => defaultError
        };

    private static string GetRevertError(EstimateGasTracer gasTracer) =>
        gasTracer.Error ?? (gasTracer.ReturnValue?.Length > 0
            ? $"{ExecutionReverted}: {gasTracer.ReturnValue.ToHexString(true)}"
            : ExecutionReverted);

    private readonly record struct EstimationBounds(ulong LeftBound, ulong RightBound, ulong IntrinsicGas);

    private readonly record struct EstimationResult(ulong GasEstimate, string? Error)
    {
        public static EstimationResult Success(ulong gasEstimate) => new(gasEstimate, null);
        public static EstimationResult Failure(string error) => new(0, error);
    }
}
