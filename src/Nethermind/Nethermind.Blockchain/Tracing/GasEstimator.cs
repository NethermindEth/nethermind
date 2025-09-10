// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
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

    private const int MaxErrorMargin = 10000;

    public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, out string? err,
        int errorMargin = DefaultErrorMargin, CancellationToken token = new())
    {
        err = null;

        if (errorMargin < 0)
        {
            err = "Invalid error margin, cannot be negative.";
            return 0;
        }
        else if (errorMargin >= MaxErrorMargin)
        {
            err = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
            return 0;
        }

        IReleaseSpec releaseSpec =
            specProvider.GetSpec(header.Number + 1, header.Timestamp + blocksConfig.SecondsPerSlot);

        tx.SenderAddress ??= Address.Zero; // If sender is not specified, use zero address.

        // Calculate and return additional gas required in case of insufficient funds.
        UInt256 senderBalance = stateProvider.GetBalance(tx.SenderAddress);
        if (tx.ValueRef != UInt256.Zero && tx.ValueRef > senderBalance && !tx.IsSystem())
        {
            long additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
            if (additionalGas == 0)
            {
                // If no additional gas can help, it's an insufficient balance error
                err = GetError(gasTracer, "insufficient balance");
            }
            return additionalGas;
        }

        var lowerBound = IntrinsicGasCalculator.Calculate(tx, releaseSpec).MinimalGas;

        // Setting boundaries for binary search - determine lowest and highest gas can be used during the estimation:
        long leftBound = (gasTracer.GasSpent != 0 && gasTracer.GasSpent >= lowerBound)
            ? gasTracer.GasSpent - 1
            : lowerBound - 1;
        long rightBound = (tx.GasLimit != 0 && tx.GasLimit >= lowerBound)
            ? tx.GasLimit
            : header.GasLimit;

        if (leftBound > rightBound)
        {
            err = "Cannot estimate gas, gas spent exceeded transaction and block gas limit";
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
            if (TryExecutableTransaction(tx, header, optimisticGasEstimate, token, gasTracer))
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
            if (!TryExecutableTransaction(tx, header, mid, token, gasTracer))
            {
                leftBound = mid;
            }
            else
            {
                rightBound = mid;
            }
        }

        if (rightBound == cap && !TryExecutableTransaction(tx, header, rightBound, token, gasTracer))
        {
            err = GetError(gasTracer);
            return 0;
        }

        return rightBound;
    }

    private static string GetError(EstimateGasTracer gasTracer, string defaultError = "Transaction execution fails") =>
        gasTracer switch
        {
            { OutOfGas: true } => "Gas estimation failed due to out of gas",
            { StatusCode: StatusCode.Failure } => gasTracer.Error ?? "Transaction execution fails",
            _ => defaultError
        };

    private bool TryExecutableTransaction(Transaction transaction, BlockHeader block, long gasLimit,
        CancellationToken token, EstimateGasTracer gasTracer)
    {
        Transaction txClone = new Transaction();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        transactionProcessor.SetBlockExecutionContext(new(block, specProvider.GetSpec(block)));
        TransactionResult result = transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        return result.TransactionExecuted && gasTracer.StatusCode == StatusCode.Success && !gasTracer.OutOfGas;
    }
}
