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
    /// Filters out transactions where gas fee properties were set too low or where the sender has not enough balance.
    /// </summary>
    internal sealed class FeeTooLowFilter : IIncomingTxFilter
    {
        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly TxDistinctSortedPool _txs;
        private readonly ILogger _logger;

        public FeeTooLowFilter(IChainHeadInfoProvider headInfo, TxDistinctSortedPool txs, ILogger logger)
        {
            _specProvider = headInfo.SpecProvider;
            _headInfo = headInfo;
            _txs = txs;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.IsFree())
            {
                return AcceptTxResult.Accepted;
            }

            bool isTrace = _logger.IsTrace;
            if (tx.GasLimit < Transaction.BaseTxGasCost)
            {
                // Not high enough GasLimit to run a txn
                Metrics.PendingTransactionsTooLowFee++;
                if (isTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low gas limit for a txn at price {tx.GasPrice}");
                return AcceptTxResult.FeeTooLow.WithMessage("GasLimit too low to start txn at GasPrice");
            }

            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            bool isEip1559Enabled = spec.IsEip1559Enabled;
            if (isEip1559Enabled && tx.IsEip1559)
            {
                if (tx.GasLimit < spec.MinGasLimit || tx.GasPrice < spec.Eip1559BaseFeeMinValue)
                {
                    // Amounts too low for spec
                    Metrics.PendingTransactionsTooLowFee++;
                    return AcceptTxResult.FeeTooLow.WithMessage("GasLimit or GasPrice too low for Eip1559 spec");
                }
            }

            UInt256 affordableGasPrice = tx.CalculateGasPrice(isEip1559Enabled, _headInfo.CurrentBaseFee);

            // Don't accept zero fee txns even if pool is empty as will never run
            if (affordableGasPrice.IsZero)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (isTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return AcceptTxResult.FeeTooLow.WithMessage("Affordable FeePerGas of 0 rejected.");
            }

            bool isLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
            if (isLocal)
            {
                return AcceptTxResult.Accepted;
            }

            if (_txs.IsFull() && _txs.TryGetLast(out Transaction? lastTx)
                && affordableGasPrice <= lastTx?.GasBottleneck)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (isTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return AcceptTxResult.FeeTooLow.WithMessage($"FeePerGas needs to be higher than {lastTx.GasBottleneck.Value} to be added to the TxPool. FeePerGas of rejected tx: {affordableGasPrice}.");
            }

            return AcceptTxResult.Accepted;
        }
    }
}
