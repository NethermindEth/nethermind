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

public readonly record struct EstimationBounds(long LeftBound, long RightBound);

public readonly record struct EstimationResult(long GasEstimate, string? Error)
{
    public bool IsSuccess => Error is null;
    public static EstimationResult Success(long gasEstimate) => new(gasEstimate, null);
    public static EstimationResult Failure(string error) => new(0, error);
}

public static class GasEstimationConstants
{
    public const int DefaultErrorMargin = 150;
    public const int MaxErrorMargin = 10000;
    public const double BasisPointsDivisor = 10000d;

    public const string InvalidErrorMarginNegative = "Invalid error margin, cannot be negative.";
    public const string InvalidErrorMarginTooHigh = "Invalid error margin, must be lower than {0}.";
    public const string GasEstimationOutOfGas = "Gas estimation failed due to out of gas";
    public const string TransactionExecutionFails = "Transaction execution fails";
    public const string InsufficientSenderBalance = "Insufficient sender balance";
    public const string CannotEstimateGasExceeded = "Cannot estimate gas, gas spent exceeded transaction and block gas limit";

}

internal interface IGasEstimationValidator
{
    EstimationResult ValidateRequest(Transaction tx, int errorMargin);
}

internal interface ITransactionFundsChecker
{
    EstimationResult CheckFunds(Transaction tx, IReleaseSpec releaseSpec, EstimateGasTracer gasTracer);
}

internal interface IEstimationBoundsCalculator
{
    EstimationResult<EstimationBounds> CalculateBounds(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, long intrinsicGas);
}

internal interface IGasEstimationStrategy
{
    EstimationResult Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
                             EstimationBounds bounds, int errorMargin, CancellationToken token);
}

internal interface ITransactionExecutor
{
    bool TryExecute(Transaction transaction, BlockHeader header, long gasLimit,
                   CancellationToken token, EstimateGasTracer gasTracer);
}

internal readonly record struct EstimationResult<T>(T Value, string? Error)
{
    public bool IsSuccess => Error is null;
    public static EstimationResult<T> Success(T value) => new(value, null);
    public static EstimationResult<T> Failure(string error) => new(default!, error);
}

internal class GasEstimationValidator : IGasEstimationValidator
{
    public EstimationResult ValidateRequest(Transaction tx, int errorMargin)
    {
        if (errorMargin < 0)
        {
            return EstimationResult.Failure(GasEstimationConstants.InvalidErrorMarginNegative);
        }

        if (errorMargin >= GasEstimationConstants.MaxErrorMargin)
        {
            return EstimationResult.Failure(string.Format(GasEstimationConstants.InvalidErrorMarginTooHigh, GasEstimationConstants.MaxErrorMargin));
        }

        return EstimationResult.Success(0);
    }
}

internal class TransactionFundsChecker : ITransactionFundsChecker
{
    private readonly IReadOnlyStateProvider _stateProvider;

    public TransactionFundsChecker(IReadOnlyStateProvider stateProvider)
    {
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
    }

    public EstimationResult CheckFunds(Transaction tx, IReleaseSpec releaseSpec, EstimateGasTracer gasTracer)
    {
        if (HasSufficientFunds(tx))
        {
            return EstimationResult.Success(0);
        }

        return HandleInsufficientFunds(tx, releaseSpec, gasTracer);
    }

    private bool HasSufficientFunds(Transaction tx)
    {
        return tx.IsSystem() || tx.ValueRef == UInt256.Zero || tx.ValueRef <= _stateProvider.GetBalance(tx.SenderAddress);
    }

    private EstimationResult HandleInsufficientFunds(Transaction tx, IReleaseSpec releaseSpec, EstimateGasTracer gasTracer)
    {
        long additionalGas = gasTracer.CalculateAdditionalGasRequired(tx, releaseSpec);
        if (additionalGas > 0)
        {
            return EstimationResult.Success(additionalGas);
        }

        if (gasTracer.OutOfGas)
        {
            return EstimationResult.Failure(GasEstimationConstants.GasEstimationOutOfGas);
        }

        if (gasTracer.StatusCode != StatusCode.Success)
        {
            return EstimationResult.Failure(gasTracer.Error ?? GasEstimationConstants.TransactionExecutionFails);
        }

        return EstimationResult.Failure(GasEstimationConstants.InsufficientSenderBalance);
    }
}

internal class EstimationBoundsCalculator : IEstimationBoundsCalculator
{
    public EstimationResult<EstimationBounds> CalculateBounds(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, long intrinsicGas)
    {
        long leftBound = Math.Max(gasTracer.GasSpent - 1, intrinsicGas - 1);
        long rightBound = (tx.GasLimit != 0 && tx.GasLimit >= intrinsicGas) ? tx.GasLimit : header.GasLimit;

        if (leftBound > rightBound)
        {
            return EstimationResult<EstimationBounds>.Failure(GasEstimationConstants.CannotEstimateGasExceeded);
        }

        var bounds = new EstimationBounds(leftBound, rightBound);
        return EstimationResult<EstimationBounds>.Success(bounds);
    }
}

internal class TransactionExecutor : ITransactionExecutor
{
    private readonly ITransactionProcessor _transactionProcessor;
    private readonly ISpecProvider _specProvider;

    public TransactionExecutor(ITransactionProcessor transactionProcessor, ISpecProvider specProvider)
    {
        _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    }

    public bool TryExecute(Transaction transaction, BlockHeader header, long gasLimit,
                          CancellationToken token, EstimateGasTracer gasTracer)
    {
        Transaction txClone = new();
        transaction.CopyTo(txClone);
        txClone.GasLimit = gasLimit;

        _transactionProcessor.SetBlockExecutionContext(new(header, _specProvider.GetSpec(header)));
        TransactionResult result = _transactionProcessor.CallAndRestore(txClone, gasTracer.WithCancellation(token));

        return result.Success && gasTracer.StatusCode == StatusCode.Success && !gasTracer.OutOfGas;
    }
}

internal class BinarySearchGasEstimationStrategy : IGasEstimationStrategy
{
    private readonly ITransactionExecutor _transactionExecutor;

    public BinarySearchGasEstimationStrategy(ITransactionExecutor transactionExecutor)
    {
        _transactionExecutor = transactionExecutor ?? throw new ArgumentNullException(nameof(transactionExecutor));
    }

    public EstimationResult Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
                                   EstimationBounds bounds, int errorMargin, CancellationToken token)
    {
        double marginMultiplier = errorMargin == 0 ? 1d : errorMargin / GasEstimationConstants.BasisPointsDivisor + 1d;
        long cap = bounds.RightBound;

        var (leftBound, rightBound) = TryOptimisticEstimate(tx, header, gasTracer, bounds, marginMultiplier, token);

        while (ShouldContinueSearch(leftBound, rightBound, marginMultiplier - 1d))
        {
            long mid = leftBound + (rightBound - leftBound) / 2;
            if (_transactionExecutor.TryExecute(tx, header, mid, token, gasTracer))
            {
                rightBound = mid;
            }
            else
            {
                leftBound = mid;
            }
        }

        return ValidateResult(tx, header, gasTracer, rightBound, cap, token);
    }

    private (long leftBound, long rightBound) TryOptimisticEstimate(Transaction tx, BlockHeader header,
        EstimateGasTracer gasTracer, EstimationBounds bounds, double marginMultiplier, CancellationToken token)
    {
        long leftBound = bounds.LeftBound;
        long rightBound = bounds.RightBound;

        long optimistic = (long)((gasTracer.GasSpent + gasTracer.TotalRefund + GasCostOf.CallStipend) * marginMultiplier);

        if (optimistic > leftBound && optimistic < rightBound)
        {
            if (_transactionExecutor.TryExecute(tx, header, optimistic, token, gasTracer))
            {
                rightBound = optimistic;
            }
            else
            {
                leftBound = optimistic;
            }
        }

        return (leftBound, rightBound);
    }

    private static bool ShouldContinueSearch(long leftBound, long rightBound, double threshold)
    {
        return (rightBound - leftBound) / (double)leftBound > threshold && leftBound + 1 < rightBound;
    }

    private EstimationResult ValidateResult(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
                                          long result, long cap, CancellationToken token)
    {
        if (result == cap && !_transactionExecutor.TryExecute(tx, header, result, token, gasTracer))
        {
            return EstimationResult.Failure(GetFailureReason(gasTracer));
        }

        if (result == 0)
        {
            return EstimationResult.Failure(GasEstimationConstants.CannotEstimateGasExceeded);
        }

        return EstimationResult.Success(result);
    }

    private static string GetFailureReason(EstimateGasTracer gasTracer)
    {
        if (gasTracer.OutOfGas)
            return GasEstimationConstants.GasEstimationOutOfGas;

        if (gasTracer.StatusCode != StatusCode.Success)
            return gasTracer.Error ?? GasEstimationConstants.TransactionExecutionFails;

        return GasEstimationConstants.CannotEstimateGasExceeded;
    }
}

public class GasEstimator
{
    private const int DefaultErrorMargin = GasEstimationConstants.DefaultErrorMargin;
    private const int MaxErrorMargin = GasEstimationConstants.MaxErrorMargin;

    private readonly IGasEstimationValidator _validator;
    private readonly ITransactionFundsChecker _fundsChecker;
    private readonly IEstimationBoundsCalculator _boundsCalculator;
    private readonly IGasEstimationStrategy _estimationStrategy;
    private readonly ISpecProvider _specProvider;
    private readonly IBlocksConfig _blocksConfig;

    public GasEstimator(ITransactionProcessor transactionProcessor, IReadOnlyStateProvider stateProvider,
        ISpecProvider specProvider, IBlocksConfig blocksConfig)
    {
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _blocksConfig = blocksConfig ?? throw new ArgumentNullException(nameof(blocksConfig));

        _validator = new GasEstimationValidator();
        _fundsChecker = new TransactionFundsChecker(stateProvider);
        _boundsCalculator = new EstimationBoundsCalculator();

        _estimationStrategy = new BinarySearchGasEstimationStrategy(new TransactionExecutor(transactionProcessor, specProvider));
    }

    public long Estimate(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer, out string? err,
        int errorMargin = DefaultErrorMargin, CancellationToken token = default)
    {
        var result = EstimateInternal(tx, header, gasTracer, errorMargin, token);
        err = result.Error;
        return result.GasEstimate;
    }

    private EstimationResult EstimateInternal(Transaction tx, BlockHeader header, EstimateGasTracer gasTracer,
                                            int errorMargin, CancellationToken token)
    {
        var validationResult = _validator.ValidateRequest(tx, errorMargin);
        if (!validationResult.IsSuccess)
            return validationResult;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(header.Number + 1, header.Timestamp + _blocksConfig.SecondsPerSlot);
        tx.SenderAddress ??= Address.Zero;

        var fundsResult = _fundsChecker.CheckFunds(tx, releaseSpec, gasTracer);
        if (!fundsResult.IsSuccess)
            return fundsResult;
        if (fundsResult.GasEstimate > 0)
            return fundsResult;

        long intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec).MinimalGas;
        var boundsResult = _boundsCalculator.CalculateBounds(tx, header, gasTracer, intrinsicGas);
        if (!boundsResult.IsSuccess)
            return EstimationResult.Failure(boundsResult.Error);

        return _estimationStrategy.Estimate(tx, header, gasTracer, boundsResult.Value, errorMargin, token);
    }
}
