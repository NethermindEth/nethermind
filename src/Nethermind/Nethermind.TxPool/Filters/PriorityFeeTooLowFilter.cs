// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

public class PriorityFeeTooLowFilter(ILogger logger, ITxPoolConfig txPoolConfig) : IIncomingTxFilter
{
    private readonly ILogger _logger = logger;
    private readonly UInt256 _minBlobsPriorityFee = txPoolConfig.MinBlobTxPriorityFee;

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (tx.SupportsBlobs && tx.MaxPriorityFeePerGas < _minBlobsPriorityFee)
        {
            Metrics.PendingTransactionsTooLowPriorityFee++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low priority fee with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxPriorityFeePerGas for blob transaction needs to be at least {_minBlobsPriorityFee} (1 GWei), is {tx.MaxPriorityFeePerGas}.");
        }

        return AcceptTxResult.Accepted;
    }
}
