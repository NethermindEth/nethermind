// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters;

public class PriorityFeeTooLowFilter : IIncomingTxFilter
{
    private readonly ILogger _logger;
    private const int OneGWei = 1_000_000_000;

    public PriorityFeeTooLowFilter(ILogger logger)
    {
        _logger = logger;
    }

    public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
    {
        if (tx.SupportsBlobs && tx.MaxPriorityFeePerGas < OneGWei)
        {
            Metrics.PendingTransactionsTooLowPriorityFee++;
            if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low priority fee with options {handlingOptions} from {new StackTrace()}");
            return AcceptTxResult.FeeTooLow.WithMessage($"MaxPriorityFeePerGas for blob transaction needs to be at least {OneGWei} (1 GWei), is {tx.MaxPriorityFeePerGas}.");
        }

        return AcceptTxResult.Accepted;
    }
}
