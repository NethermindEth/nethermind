// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

public class PriorityFeeTooLowFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig, ILogger logger) : IIncomingTxFilter
{
    private readonly ILogger _logger = logger;
    private readonly IChainHeadInfoProvider _chainHeadInfoProvider = chainHeadInfoProvider;
    private readonly UInt256 _minBlobsPriorityFee = txPoolConfig.MinBlobTxPriorityFee;
    private static int _minBlobBaseFeePercent = txPoolConfig.MinFeePerBlobGasPercentRequirement;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (!tx.SupportsBlobs)
        {
            return AcceptTxResult.Accepted;
        }

        if (tx.MaxPriorityFeePerGas < _minBlobsPriorityFee)
        {
            Metrics.PendingTransactionsTooLowPriorityFee++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low priority fee with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxPriorityFeePerGas for blob transaction needs to be at least {_minBlobsPriorityFee} (1 GWei), is {tx.MaxPriorityFeePerGas}.");
        }

        // quick path - allowing txs with max fee per blob gas higher than current fee per blob gas
        if (tx.MaxFeePerBlobGas >= _chainHeadInfoProvider.CurrentFeePerBlobGas)
        {
            return AcceptTxResult.Accepted;
        }

        // and slow one
        bool overflow = UInt256.MultiplyOverflow(_chainHeadInfoProvider.CurrentFeePerBlobGas, (UInt256)_minBlobBaseFeePercent, out UInt256 minFeePerBlobGas);
        UInt256.Divide(minFeePerBlobGas, 100, out minFeePerBlobGas);
        if (!overflow && tx.MaxFeePerBlobGas < minFeePerBlobGas)
        {
            Metrics.PendingTransactionsTooLowFeePerBlobGas++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low blob fee per gas with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxFeePerBlobGas needs to be at least {_minBlobBaseFeePercent} percent of CurrentFeePerBlobGas ({_chainHeadInfoProvider.CurrentFeePerBlobGas}), is {tx.MaxFeePerBlobGas}.");
        }

        return AcceptTxResult.Accepted;
    }
}
