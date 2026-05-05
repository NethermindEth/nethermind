// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
    public const string InsufficientBalance = "insufficient funds for transfer";

    private const int MaxErrorMargin = 10000;
    private const double BasisPointsDivisor = 10000d;

    private const string InvalidErrorMarginNegative = "Invalid error margin, cannot be negative.";
    private static readonly string InvalidErrorMarginTooHigh = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
    private const string GasEstimationOutOfGas = "Gas estimation failed due to out of gas";
    private const string TransactionExecutionFails = "Transaction execution fails";
    private const string CannotEstimateGasExceeded = "Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap";
    private const string ExecutionReverted = "execution reverted";

    public long Estimate(
        Transaction tx,
        BlockHeader header,
        EstimateGasTracer gasTracer,
        out string? err,
        int errorMargin = DefaultErrorMargin,
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
        int errorMargin,
        CancellationToken token)
    {
        if (ValidateErrorMargin(errorMargin) is { } validationError)
            return validationError;

        IReleaseSpec spec = specProvider.GetSpec(header.Number + 1, header.Timestamp + blocksConfig.SecondsPerSlot);
        tx.SenderAddress ??= Address.Zero;

        UInt256 senderBalance = stateProvider.GetBalance(tx.SenderAddress!);

        if (CheckFunds(tx, spec, gasTracer, senderBalance) is { } fundsResult)
            return fundsResult;

        long intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec).MinimalGas;
        long leftBound = Math.Max(gasTracer.GasSpent - 1, intrinsicGas - 1);
        long rightBound = Math.Min(
            tx.GasLimit != 0 && tx.GasLimit >= intrinsicGas ? tx.GasLimit : header.GasLimit,
            spec.GetTxGasLimitCap());

        if (leftBound > rightBound)
            return EstimationResult.Failure(CannotEstimateGasExceeded);

        UInt256 feeCap = tx.CalculateFeeCap();
        // tx.ValueRef <= senderBalance is guaranteed here; subtract so the cap reflects gas budget only.
        EstimationBounds bounds = CapByAllowance(new EstimationBounds(leftBound, rightBound, intrinsicGas), senderBalance - tx.ValueRef, feeCap);

        return BinarySearchEstimate(tx, header, gasTracer, bounds, errorMargin, token);
    }

    private static EstimationResult? ValidateErrorMargin(int errorMargin) =>
        errorMargin switch
        {
            < 0 => EstimationResult.Failure(InvalidErrorMarginNegative),
            >= MaxErrorMargin => EstimationResult.Failure(InvalidErrorMarginTooHigh),
            _ => null
        };

    // Returns null if funds are sufficient (estimation continues), or a terminal result to return immediately.
    private static EstimationResult? CheckFunds(Transaction tx, IReleaseSpec spec, EstimateGasTracer gasTracer, UInt256 senderBalance)
    {
        if (tx.ValueRef == UInt256.Zero || tx.ValueRef <= senderBalance)
            return null;

        long additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, spec);
        return additionalGas > 0
            ? EstimationResult.Success(additionalGas)
            : EstimationResult.Failure(GetError(gasTracer, InsufficientBalance));
    }

    private static EstimationBounds CapByAllowance(EstimationBounds bounds, UInt256 available, UInt256 feeCap = default)
    {
        if (feeCap == UInt256.Zero)
            return bounds;

        long allowance = (long)UInt256.Min(available / feeCap, (UInt256)long.MaxValue);
        return bounds with { RightBound = Math.Min(bounds.RightBound, allowance) };
    }

    private EstimationResult BinarySearchEstimate(
        Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
        EstimationBounds bounds, int errorMargin, CancellationToken token)
    {
        // Short-circuit: simple ETH transfers need exactly the intrinsic gas.
        if (IsSimpleTransfer(tx) && TryExecute(tx, header, bounds.IntrinsicGas, gasTracer, token, out _))
            return EstimationResult.Success(bounds.IntrinsicGas);

        // Execute at maximum gas first (Geth parity): gas-related failure → allowance error; other → surface directly.
        if (!TryExecute(tx, header, bounds.RightBound, gasTracer, token, out bool isGasRelatedFailure))
        {
            string error = (gasTracer.OutOfGas || isGasRelatedFailure)
                ? $"{GasExceedsAllowanceMsgPrefix} ({bounds.RightBound})"
                : GetError(gasTracer);
            return EstimationResult.Failure(error);
        }

        double marginMultiplier = errorMargin == 0 ? 1d : errorMargin / BasisPointsDivisor + 1d;
        long cap = bounds.RightBound;
        (long leftBound, long rightBound) = TryOptimisticEstimate(tx, header, gasTracer, bounds, marginMultiplier, token);

        // Narrow bounds until within the error margin (Geth approach).
        while (ShouldContinueSearch(leftBound, rightBound, marginMultiplier - 1d))
        {
            long mid = leftBound + (rightBound - leftBound) / 2;
            if (TryExecute(tx, header, mid, gasTracer, token, out _))
                rightBound = mid;
            else
                leftBound = mid;
        }

        if (rightBound == cap && !TryExecute(tx, header, rightBound, gasTracer, token, out _))
            return EstimationResult.Failure(GetError(gasTracer));

        return EstimationResult.Success(rightBound);
    }

    private (long Left, long Right) TryOptimisticEstimate(
        Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
        EstimationBounds bounds, double marginMultiplier, CancellationToken token)
    {
        long leftBound = bounds.LeftBound;
        long rightBound = bounds.RightBound;

        // Optimistic first guess (Geth approach): reduces binary search iterations in most cases.
        long optimistic = (long)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * marginMultiplier);
        if (optimistic > leftBound && optimistic < rightBound)
        {
            if (TryExecute(tx, header, optimistic, gasTracer, token, out _))
                rightBound = optimistic;
            else
                leftBound = optimistic;
        }

        return (leftBound, rightBound);
    }

    private bool TryExecute(Transaction transaction, BlockHeader header, long gasLimit,
                             EstimateGasTracer gasTracer, CancellationToken token, out bool isGasRelatedFailure)
    {
        Transaction txClone = new();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(header, specProvider.GetSpec(header)));
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
            or TransactionResult.ErrorType.BlockGasLimitExceeded;

    private static bool ShouldContinueSearch(long leftBound, long rightBound, double threshold) =>
        (rightBound - leftBound) / (double)leftBound > threshold && leftBound + 1 < rightBound;

    private static bool IsSimpleTransfer(Transaction tx) =>
        tx.To is not null && tx.Data.IsEmpty;

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

    private readonly record struct EstimationBounds(long LeftBound, long RightBound, long IntrinsicGas);

    private readonly record struct EstimationResult(long GasEstimate, string? Error)
    {
        public static EstimationResult Success(long gasEstimate) => new(gasEstimate, null);
        public static EstimationResult Failure(string error) => new(0, error);
    }
}
