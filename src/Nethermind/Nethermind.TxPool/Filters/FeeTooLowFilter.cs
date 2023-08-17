// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        private readonly TxDistinctSortedPool _blobTxs;
        private readonly bool _thereIsPriorityContract;
        private readonly ILogger _logger;

        public FeeTooLowFilter(IChainHeadInfoProvider headInfo, TxDistinctSortedPool txs, TxDistinctSortedPool blobTxs, bool thereIsPriorityContract, ILogger logger)
        {
            _specProvider = headInfo.SpecProvider;
            _headInfo = headInfo;
            _txs = txs;
            _blobTxs = blobTxs;
            _thereIsPriorityContract = thereIsPriorityContract;
            _logger = logger;
        }

        public AcceptTxResult Accept(Transaction tx, TxFilteringState state, TxHandlingOptions handlingOptions)
        {
            if (tx.SupportsBlobs && tx.MaxPriorityFeePerGas < 1.GWei())
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return AcceptTxResult.FeeTooLow.WithMessage($"MaxPriorityFeePerGas for blob transaction needs to be at least {1.GWei()} (1 GWei), is {tx.MaxPriorityFeePerGas}.");
            }

            bool isLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) != 0;
            if (isLocal)
            {
                return AcceptTxResult.Accepted;
            }

            IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();
            bool isEip1559Enabled = spec.IsEip1559Enabled;
            UInt256 affordableGasPrice = tx.CalculateGasPrice(isEip1559Enabled, _headInfo.CurrentBaseFee);
            // Don't accept zero fee txns even if pool is empty as will never run
            if (isEip1559Enabled && !_thereIsPriorityContract && !tx.IsFree() && affordableGasPrice.IsZero)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                return !isLocal ?
                    AcceptTxResult.FeeTooLow :
                    AcceptTxResult.FeeTooLow.WithMessage("Affordable gas price is 0");
            }

            TxDistinctSortedPool relevantPool = (tx.SupportsBlobs ? _blobTxs : _txs);
            if (relevantPool.IsFull() && relevantPool.TryGetLast(out Transaction? lastTx)
                && affordableGasPrice <= lastTx?.GasBottleneck)
            {
                Metrics.PendingTransactionsTooLowFee++;
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, too low payable gas price with options {handlingOptions} from {new StackTrace()}");
                }

                return !isLocal ?
                    AcceptTxResult.FeeTooLow :
                    AcceptTxResult.FeeTooLow.WithMessage($"FeePerGas needs to be higher than {lastTx.GasBottleneck.Value} to be added to the TxPool. FeePerGas of rejected tx: {affordableGasPrice}.");

            }

            return AcceptTxResult.Accepted;
        }
    }
}
