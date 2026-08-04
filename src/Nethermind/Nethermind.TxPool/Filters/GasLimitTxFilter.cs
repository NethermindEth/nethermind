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
        private readonly ILogger _logger = logManager.GetClassLogger<GasLimitTxFilter>();
        private readonly ulong _configuredGasLimit = txPoolConfig.GasLimit ?? ulong.MaxValue;

        public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            ulong gasLimit = Math.Min(chainHeadInfoProvider.BlockGasLimit ?? ulong.MaxValue, _configuredGasLimit);

            // A frame transaction's GasLimit is only the sum of its frame gas limits, so gating on it alone would
            // admit transactions whose reservation can never fit in a block.
            ulong txGasBudget = tx.SupportsFrames
                    && FrameTxValidation.TryCalculateGasBudget(tx, chainHeadInfoProvider.SpecProvider.GetCurrentHeadSpec(), out _, out _, out ulong frameTxMaxGas)
                ? frameTxMaxGas
                : tx.GasLimit;

            if (txGasBudget > gasLimit)
            {
                Metrics.PendingTransactionsGasLimitTooHigh++;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.GasLimitExceeded :
                    AcceptTxResult.GasLimitExceeded.WithMessage($"Gas limit: {gasLimit}, gas limit of rejected tx: {txGasBudget}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
