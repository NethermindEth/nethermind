// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

public class PriorityFeeTooLowFilter : IIncomingTxFilter
{
    private readonly ILogger _logger;
    private readonly IChainHeadInfoProvider _chainHeadInfoProvider;
    private static readonly UInt256 _minBlobsPriorityFee = 1.GWei();
    private static int _minBlobBaseFeePercent;

    public PriorityFeeTooLowFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig, ILogger logger)
    {
        _chainHeadInfoProvider = chainHeadInfoProvider;
        _logger = logger;
        _minBlobBaseFeePercent = txPoolConfig.MinBlobBaseFeePercentRequirement;
    }

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
            Metrics.PendingTransactionsTooFeePerBlobGas++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low blob fee per gas with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxFeePerBlobGas needs to be at least {_minBlobBaseFeePercent} percent of CurrentFeePerBlobGas ({_chainHeadInfoProvider.CurrentFeePerBlobGas}), is {tx.MaxFeePerBlobGas}.");
        }

        return AcceptTxResult.Accepted;
    }
}
