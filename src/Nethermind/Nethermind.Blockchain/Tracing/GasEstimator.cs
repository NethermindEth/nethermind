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
    /// <summary>
    /// Error margin used if none other is specified expressed in basis points.
    /// </summary>
    public const int DefaultErrorMargin = 150;

    /// <summary>Error-message prefix emitted when the required gas exceeds what the sender can afford.</summary>
    public const string AllowanceExceedanceMsgPrefix = "gas required exceeds allowance";

    private const int MaxErrorMargin = 10000;

    public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, out string? err,
        int errorMargin = DefaultErrorMargin, CancellationToken token = new())
    {
        err = null;

        switch (errorMargin)
        {
            case < 0:
                err = "Invalid error margin, cannot be negative.";
                return 0;
            case >= MaxErrorMargin:
                err = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
                return 0;
        }

        IReleaseSpec releaseSpec = specProvider.GetSpec(header.Number + 1, header.Timestamp + blocksConfig.SecondsPerSlot);

        tx.SenderAddress ??= Address.Zero; // If sender is not specified, use zero address.

        // Calculate and return additional gas required in case of insufficient funds.
        UInt256 senderBalance = stateProvider.GetBalance(tx.SenderAddress);
        if (tx.ValueRef != UInt256.Zero && tx.ValueRef > senderBalance)
        {
            long additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
            if (additionalGas == 0)
            {
                // If no additional gas can help, it's an insufficient balance error
                err = GetError(gasTracer, "insufficient balance");
            }
            return additionalGas;
        }

        // tx.ValueRef <= senderBalance is guaranteed for non-system transactions (early return above handles the opposite).
        // Subtract value so the gas allowance cap reflects only what is available for gas, matching Geth's behavior.
        UInt256 available = senderBalance - tx.ValueRef;

        long lowerBound = IntrinsicGasCalculator.Calculate(tx, releaseSpec).MinimalGas;

        // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
        long leftBound = gasTracer.GasSpent != 0 && gasTracer.GasSpent >= lowerBound
            ? gasTracer.GasSpent - 1
            : lowerBound - 1;
        long rightBound = tx.GasLimit != 0 && tx.GasLimit >= lowerBound
            ? tx.GasLimit
            : header.GasLimit;
        rightBound = Math.Min(rightBound, releaseSpec.GetTxGasLimitCap());

        if (leftBound > rightBound)
        {
            err = "Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap";
            return 0;
        }

        // Cap rightBound to what the sender can afford (Geth parity: allowance = (balance - value) / gasPrice).
        // With the shrunk gas limit the TransactionProcessor balance check passes, the EVM runs,
        // and fails at intrinsic gas with OOG — producing "gas required exceeds allowance (N)".
        if (tx.MaxFeePerGas > UInt256.Zero)
        {
            long allowance = (long)UInt256.Min(available / tx.MaxFeePerGas, (UInt256)long.MaxValue);
            rightBound = Math.Min(rightBound, allowance);
        }

        // If transaction is simple transfer return intrinsic gas
        if (tx.To is not null && tx.Data.IsEmpty && TryExecutableTransaction(tx, header, lowerBound, gasTracer, token)) return lowerBound;

        // Execute at the highest allowable gas limit first (Geth parity).
        // If it fails with OOG (or another gas-related pre-check failure), return allowance exceeded.
        // If it fails for a non-gas reason (for example revert), return that error immediately.
        if (!TryExecutableTransaction(tx, header, rightBound, gasTracer, token, out bool isGasRelatedFailure))
        {
            err = (gasTracer.OutOfGas || isGasRelatedFailure)
                ? $"{AllowanceExceedanceMsgPrefix} ({rightBound})"
                : GetError(gasTracer);

            return 0;
        }

        // Execute binary search to find the optimal gas estimation.
        return BinarySearchEstimate(leftBound, rightBound, tx, header, gasTracer, errorMargin, token, out err);
    }

    private long BinarySearchEstimate(
        long leftBound,
        long rightBound,
        Transaction tx,
        BlockHeader header,
        EstimateGasTracer gasTracer,
        int errorMargin,
        CancellationToken token,
        out string? err)
    {
        err = null;
        double marginWithDecimals = errorMargin == 0 ? 1 : errorMargin / 10000d + 1;

        //This approach is similar to Geth, by starting from an optimistic guess the number of iterations is greatly reduced in most cases
        long optimisticGasEstimate = (long)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * marginWithDecimals);
        if (optimisticGasEstimate > leftBound && optimisticGasEstimate < rightBound)
        {
            if (TryExecutableTransaction(tx, header, optimisticGasEstimate, gasTracer, token))
                rightBound = optimisticGasEstimate;
            else
                leftBound = optimisticGasEstimate;
        }

        long cap = rightBound;
        //This is similar to Geth's approach by stopping, when the estimation is within a certain margin of error
        while ((rightBound - leftBound) / (double)leftBound > (marginWithDecimals - 1)
               && leftBound + 1 < rightBound)
        {
            long mid = (leftBound + rightBound) / 2;
            if (!TryExecutableTransaction(tx, header, mid, gasTracer, token))
            {
                leftBound = mid;
            }
            else
            {
                rightBound = mid;
            }
        }

        if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound, gasTracer, token))
        {
            err = GetError(gasTracer);
            return 0;
        }

        return rightBound;
    }

    private static string GetError(EstimateGasTracer gasTracer, string defaultError = "Transaction execution fails") =>
        gasTracer switch
        {
            { TopLevelRevert: true } => gasTracer.Error ??
                                        (gasTracer.ReturnValue?.Length > 0 ?
                                            $"execution reverted: {gasTracer.ReturnValue.ToHexString(true)}"
                                            : "execution reverted"),
            { OutOfGas: true } => "Gas estimation failed due to out of gas",
            { StatusCode: StatusCode.Failure } => gasTracer.Error ?? "Transaction execution fails",
            _ => defaultError
        };

    private static bool IsGasRelatedExecutionFailure(TransactionResult result) =>
        result.Error is TransactionResult.ErrorType.GasLimitBelowIntrinsicGas
            or TransactionResult.ErrorType.BlockGasLimitExceeded;

    private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit,
        EstimateGasTracer gasTracer, CancellationToken token)
        => TryExecutableTransaction(transaction, block, gasLimit, gasTracer, token, out _);

    private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit,
        EstimateGasTracer gasTracer, CancellationToken token, out bool isGasRelatedFailure)
    {
        Transaction txClone = new();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block, specProvider.GetSpec(block)));
        TransactionResult callResult = transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        if (IsGasRelatedExecutionFailure(callResult))
        {
            isGasRelatedFailure = true;
            return false;
        }

        isGasRelatedFailure = false;

        // Transaction succeeds if it executed, has success status, no OutOfGas, and no top-level revert
        return callResult.TransactionExecuted && gasTracer.StatusCode == StatusCode.Success &&
               !gasTracer.OutOfGas && !gasTracer.TopLevelRevert;
    }
}
