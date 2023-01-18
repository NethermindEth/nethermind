// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool.Filters
{
    /// <summary>
    /// Filters out transactions where the sender has not enough balance.
    /// </summary>
    internal class BalanceTooLowFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public BalanceTooLowFilter(IChainHeadInfoProvider headInfo, TxDistinctSortedPool txs, ILogManager logManager)
        {
            _specProvider = headInfo.SpecProvider;
            _headInfo = headInfo;
            _txs = txs;
            _logger = logManager.GetClassLogger();
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            bool islocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
            if (islocal || !_txs.IsFull())
            {
                return AcceptTxResult.Accepted;
            }

            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            UInt256 balance = state.SenderAccount.Balance;
            UInt256 affordableGasPrice = tx.CalculateAffordableGasPrice(spec.IsEip1559Enabled, _headInfo.CurrentBaseFee, balance);

            if (_txs.TryGetLast(out Transaction? lastTx)
                && affordableGasPrice <= lastTx?.GasBottleneck)
            {
                Metrics.PendingTransactionsBalanceToLowToCompeteOnFee++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return AcceptTxResult.InsufficientFundsToCompete.WithMessage($"FeePerGas needs to be higher than {lastTx.GasBottleneck.Value} to be added to the TxPool. Affordable FeePerGas of rejected tx: {affordableGasPrice}.");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
