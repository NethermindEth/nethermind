// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing;

public class GasEstimator
{
    /// <summary>
    /// Error margin used if none other is specified expressed in basis points.
    /// </summary>
    public const int DefaultErrorMargin = 150;
    private const int MaxErrorMargin = 10000;

    private readonly ITransactionProcessor _transactionProcessor;
    private readonly IReadOnlyStateProvider _stateProvider;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;

    public GasEstimator(ITransactionProcessor transactionProcessor, IReadOnlyStateProvider stateProvider,
        ISpecProvider specProvider, IBlocksConfig blocksConfig)
    {
        _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));
    }

    public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, out string? err,
        int errorMargin = DefaultErrorMargin, CancellationToken token = default)
    {
        // Validate inputs
        if (tx == null)
        {
            err = "Transaction cannot be null";
            return 0;
        }
        if (header == null)
        {
            err = "Header cannot be null";
            return 0;
        }
        if (gasTracer == null)
        {
            err = "Gas tracer cannot be null";
            return 0;
        }

        if (!IsValidErrorMargin(errorMargin, out err))
            return 0;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);
        tx.SenderAddress ??= Address.Zero;

        // Handle insufficient funds early
        if (HasInsufficientFunds(tx))
        {
            return HandleInsufficientFunds(tx, releaseSpec, gasTracer, out err);
        }

        // Calculate search boundaries
        long intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec).MinimalGas;
        if (!TryGetSearchBounds(tx, header, gasTracer, intrinsicGas, out long leftBound, out long rightBound, out err))
            return 0;

        // Perform binary search
        return BinarySearchEstimate(leftBound, rightBound, tx, header, gasTracer, errorMargin, token, out err);
    }

    private static bool IsValidErrorMargin(int errorMargin, out string? err)
    {
        if (errorMargin < 0)
        {
            err = "Invalid error margin, cannot be negative.";
            return false;
        }
        if (errorMargin >= MaxErrorMargin)
        {
            err = $"Invalid error margin, must be lower than {MaxErrorMargin}.";
            return false;
        }
        err = null;
        return true;
    }

    private bool HasInsufficientFunds(Transaction tx)
    {
        if (tx.IsSystem() || tx.ValueRef.IsZero)
            return false;

        UInt256 senderBalance = _stateProvider.GetBalance(tx.SenderAddress);
        return tx.ValueRef > senderBalance;
    }

    private static long HandleInsufficientFunds(Transaction tx, IReleaseSpec releaseSpec,
        EstimateGasTracer gasTracer, out string? err)
    {
        long additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
        if (additionalGas > 0)
        {
            err = null;
            return additionalGas;
        }

        if (gasTracer.OutOfGas)
        {
            err = "Gas estimation failed due to out of gas";
            return 0;
        }

        if (gasTracer.StatusCode != StatusCode.Success)
        {
            err = gasTracer.Error ?? "Transaction execution always fails";
            return 0;
        }

        err = "Insufficient sender balance";
        return 0;
    }

    private static bool TryGetSearchBounds(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
        long intrinsicGas, out long leftBound, out long rightBound, out string? err)
    {
        leftBound = Math.Max(gasTracer.GasSpent - 1, intrinsicGas - 1);
        rightBound = (tx.GasLimit != 0 && tx.GasLimit >= intrinsicGas) ? tx.GasLimit : header.GasLimit;

        if (leftBound > rightBound)
        {
            err = "Cannot estimate gas, gas spent exceeded transaction and block gas limit";
            return false;
        }

        err = null;
        return true;
    }

    private long BinarySearchEstimate(long leftBound, long rightBound, Transaction tx, BlockHeader header,
        EstimateGasTracer gasTracer, int errorMargin, CancellationToken token, out string? err)
    {
        long cap = rightBound;

        // Try optimistic estimation first (Geth-inspired 64/63 gas refund factor)
        if (TryOptimisticEstimate(leftBound, rightBound, gasTracer, tx, header, token, out long newLeft, out long newRight))
        {
            leftBound = newLeft;
            rightBound = newRight;
        }

        // Binary search with early termination
        while (leftBound + 1 < rightBound)
        {
            // Early termination if within acceptable error margin (skip if errorMargin is 0)
            if (errorMargin > 0)
            {
                double errorRatio = errorMargin / 10000.0;
                if ((double)(rightBound - leftBound) / rightBound < errorRatio)
                    break;
            }

            long mid = CalculateMidpoint(leftBound, rightBound);

            if (TryExecuteTransaction(tx, header, mid, gasTracer, token))
                rightBound = mid;
            else
                leftBound = mid;
        }

        // Final validation
        if (rightBound == cap && !TryExecuteTransaction(tx, header, rightBound, gasTracer, token))
        {
            err = GetFailureReason(gasTracer);
            return 0;
        }

        err = null;
        return rightBound;
    }

    private bool TryOptimisticEstimate(long leftBound, long rightBound, EstimateGasTracer gasTracer,
        Transaction tx, BlockHeader header, CancellationToken token, out long newLeft, out long newRight)
    {
        newLeft = leftBound;
        newRight = rightBound;

        // Calculate optimistic estimate with overflow protection
        long gasSpent = Math.Max(gasTracer.GasSpent, 1); // Ensure non-zero
        long refund = gasTracer.TotalRefund;
        long stipend = GasCostOf.CallStipend;

        // Check for potential overflow before multiplication
        if (gasSpent > long.MaxValue / 64 - refund - stipend)
            return false; // Skip optimistic estimation if overflow risk

        long optimistic = (gasSpent + refund + stipend) * 64 / 63;

        if (optimistic > leftBound && optimistic < rightBound)
        {
            if (TryExecuteTransaction(tx, header, optimistic, gasTracer, token))
                newRight = optimistic;
            else
                newLeft = optimistic;
            return true;
        }

        return false;
    }

    private static long CalculateMidpoint(long leftBound, long rightBound)
    {
        long mid = leftBound + (rightBound - leftBound) / 2;

        // Geth's asymmetric midpoint selection - favor lower gas estimates
        // But ensure we don't go below leftBound + 1
        if (mid > leftBound * 2)
            mid = Math.Max(leftBound * 2, leftBound + 1);

        return mid;
    }

    private bool TryExecuteTransaction(Transaction originalTx, BlockHeader header, long gasLimit,
        EstimateGasTracer gasTracer, CancellationToken token)
    {
        // Create transaction clone with modified gas limit
        Transaction txClone = new();
        originalTx.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        // Execute transaction
        var context = new BlockExecutionContext(header, _specProvider.GetSpec(header));
        _transactionProcessor.SetBlockExecutionContext(context);
        TransactionResult result = _transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        return result.Success && gasTracer.StatusCode == StatusCode.Success && !gasTracer.OutOfGas;
    }

    private static string GetFailureReason(EstimateGasTracer gasTracer)
    {
        if (gasTracer.OutOfGas)
            return "Gas estimation failed due to out of gas";

        if (gasTracer.StatusCode != StatusCode.Success)
            return gasTracer.Error ?? "Transaction execution always fails";

        return "Cannot estimate gas, gas spent exceeded transaction and block gas limit";
    }
}
