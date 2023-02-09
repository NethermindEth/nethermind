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
    internal class GasLimitTxFilter : IIncomingTxFilter
    {
        private readonly IChainHeadInfoProvider _chainHeadInfoProvider;
        private readonly ILogger _logger;
        private readonly long _configuredGasLimit;

        public GasLimitTxFilter(IChainHeadInfoProvider chainHeadInfoProvider, ITxPoolConfig txPoolConfig,
            ILogger logger)
        {
            _chainHeadInfoProvider = chainHeadInfoProvider;
            _logger = logger;
            _configuredGasLimit = txPoolConfig.GasLimit ?? long.MaxValue;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            long gasLimit = Math.Min(_chainHeadInfoProvider.BlockGasLimit ?? long.MaxValue, _configuredGasLimit);
            if (tx.GasLimit > gasLimit)
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                return AcceptTxResult.GasLimitExceeded.WithMessage($"Gas limit: {gasLimit}, gas limit of rejected tx: {tx.GasLimit}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
