// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private const double BasisPointsDivisor = 10000d;

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
        if (!IsValidErrorMargin(errorMargin, out err))
            return 0;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);
        tx.SenderAddress ??= Address.Zero;

        if (HasInsufficientFunds(tx))
        {
            return HandleInsufficientFunds(tx, releaseSpec, gasTracer, out err);
        }

        long intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec).MinimalGas;

        if (!TryGetSearchBounds(tx, header, gasTracer, intrinsicGas, out long leftBound, out long rightBound, out err))
            return 0;

        return BinarySearchEstimate(leftBound, rightBound, tx, header, gasTracer, errorMargin, token, out err);
    }

    private static bool IsValidErrorMargin(int errorMargin, out string? err)
    {
        err = null;
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
        return true;
    }

    private bool HasInsufficientFunds(Transaction tx)
    {
        return !tx.IsSystem() && tx.ValueRef != UInt256.Zero && tx.ValueRef > _stateProvider.GetBalance(tx.SenderAddress);
    }

    private static long HandleInsufficientFunds(Transaction tx, IReleaseSpec releaseSpec, EstimateGasTracer gasTracer, out string? err)
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
            err = gasTracer.Error ?? "Transaction execution fails";
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
        err = null;
        double marginMultiplier = errorMargin == 0 ? 1d : errorMargin / BasisPointsDivisor + 1d;
        long cap = rightBound;

        TryOptimisticEstimate(leftBound, rightBound, tx, header, gasTracer, marginMultiplier, token,
            out leftBound, out rightBound);

        while (ShouldContinueSearch(leftBound, rightBound, marginMultiplier - 1d))
        {
            long mid = leftBound + (rightBound - leftBound) / 2;
            if (TryExecuteTransaction(tx, header, mid, token, gasTracer))
                rightBound = mid;
            else
                leftBound = mid;
        }

        return ValidateResult(tx, header, gasTracer, rightBound, cap, token, out err);
    }

    private void TryOptimisticEstimate(long leftBound, long rightBound, Transaction tx, BlockHeader header,
        EstimateGasTracer gasTracer, double marginMultiplier, CancellationToken token,
        out long newLeftBound, out long newRightBound)
    {
        newLeftBound = leftBound;
        newRightBound = rightBound;

        long optimistic = (long)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * marginMultiplier);
        if (optimistic > leftBound && optimistic < rightBound)
        {
            if (TryExecuteTransaction(tx, header, optimistic, token, gasTracer))
                newRightBound = optimistic;
            else
                newLeftBound = optimistic;
        }
    }

    private static bool ShouldContinueSearch(long leftBound, long rightBound, double threshold)
    {
        return (rightBound - leftBound) / (double)leftBound > threshold && leftBound + 1 < rightBound;
    }

    private long ValidateResult(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
        long result, long cap, CancellationToken token, out string? err)
    {
        if (result == cap && !TryExecuteTransaction(tx, header, result, token, gasTracer))
        {
            err = GetFailureReason(gasTracer);
            return 0;
        }

        if (result == 0)
        {
            err = "Cannot estimate gas, gas spent exceeded transaction and block gas limit";
            return 0;
        }

        err = null;
        return result;
    }

    private static string GetFailureReason(EstimateGasTracer gasTracer)
    {
        if (gasTracer.OutOfGas)
            return "Gas estimation failed due to out of gas";

        if (gasTracer.StatusCode != StatusCode.Success)
            return gasTracer.Error ?? "Transaction execution fails";

        return "Cannot estimate gas, gas spent exceeded transaction and block gas limit";
    }

    private bool TryExecuteTransaction(Transaction transaction, BlockHeader block, long gasLimit,
        CancellationToken token, EstimateGasTracer gasTracer)
    {
        Transaction txClone = new();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        _transactionProcessor.SetBlockExecutionContext(new(block, _specProvider.GetSpec(block)));
        TransactionResult result = _transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        return result.TransactionExecuted && gasTracer.StatusCode == StatusCode.Success && !gasTracer.OutOfGas;
    }
}
