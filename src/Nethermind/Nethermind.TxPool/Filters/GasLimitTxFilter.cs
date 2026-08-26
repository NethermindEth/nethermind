// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
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

            IReleaseSpec spec = chainHeadInfoProvider.SpecProvider.GetCurrentHeadSpec();
            bool exceedsLimit;
            ulong rejectedBudget;
            if (tx.SupportsFrames)
            {
                bool calculated = FrameTxValidation.TryCalculateBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation);
                rejectedBudget = calculated ? Math.Max(executionReservation, stateReservation) : ulong.MaxValue;
                exceedsLimit = !calculated || executionReservation > gasLimit || stateReservation > gasLimit;
            }
            else
            {
                rejectedBudget = tx.GasLimit;
                exceedsLimit = rejectedBudget > gasLimit;
            }

            if (exceedsLimit)
            {
                Metrics.PendingTransactionsGasLimitTooHigh++;

                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
                }

                bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
                return isNotLocal ?
                    AcceptTxResult.GasLimitExceeded :
                    AcceptTxResult.GasLimitExceeded.WithMessage($"Gas limit: {gasLimit}, gas limit of rejected tx: {rejectedBudget}");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
