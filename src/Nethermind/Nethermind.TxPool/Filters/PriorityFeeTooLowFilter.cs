// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

public class PriorityFeeTooLowFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig, ILogger logger) : IIncomingTxFilter
{
    private readonly UInt256 _minBlobsPriorityFee = txPoolConfig.MinBlobTxPriorityFee;
    private readonly bool _currentBlobBaseFeeRequired = txPoolConfig.CurrentBlobBaseFeeRequired;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (!tx.SupportsBlobs)
        {
            return AcceptTxResult.Accepted;
        }

        if (tx.MaxPriorityFeePerGas < _minBlobsPriorityFee)
        {
            Metrics.PendingTransactionsTooLowPriorityFee++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low priority fee with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxPriorityFeePerGas for blob transaction needs to be at least {_minBlobsPriorityFee}, is {tx.MaxPriorityFeePerGas}.");
        }

        if (_currentBlobBaseFeeRequired && tx.MaxFeePerBlobGas < chainHeadInfoProvider.CurrentFeePerBlobGas)
        {
            Metrics.PendingTransactionsTooLowFeePerBlobGas++;
            if (logger.IsTrace) logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low blob fee per gas with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxFeePerBlobGas needs to be at least equal to CurrentFeePerBlobGas ({chainHeadInfoProvider.CurrentFeePerBlobGas}), is {tx.MaxFeePerBlobGas}.");
        }

        return AcceptTxResult.Accepted;
    }
}
