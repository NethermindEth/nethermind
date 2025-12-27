// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Ignores transactions that outright exceed block gas limit or configured max block gas limit.
    /// </summary>
    internal sealed class GasLimitTxFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig, ILogManager logManager)
        : IIncomingTxFilter
    {
        private readonly ILogger _logger = logManager.GetClassLogger();
        private readonly ulong _configuredGasLimit = txPoolConfig.GasLimit is null
            ? ulong.MaxValue
            : checked((ulong)txPoolConfig.GasLimit.Value);

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            ulong gasLimit = Math.Min(chainHeadInfoProvider.BlockGasLimit ?? ulong.MaxValue, _configuredGasLimit);
            if (tx.GasLimit > gasLimit)
            {
                Metrics.PendingTransactionsGasLimitTooHigh++;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.GasLimitExceeded :
                    AcceptTxResult.GasLimitExceeded.WithMessage($"Gas limit: {gasLimit}, gas limit of rejected tx: {tx.GasLimit}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
